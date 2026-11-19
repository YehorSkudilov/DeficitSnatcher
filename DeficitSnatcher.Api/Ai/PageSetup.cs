using System.Text;
using System.Text.Json;
using DeficitSnatcher.Api.Buying;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Ai;

/// <summary>What the AI suggests for a page, and what a check with it found.</summary>
public sealed record SetupSuggestion(
    string? Name,
    SiteKind Kind,
    string? PriceSelector,
    string? TitleSelector,
    string? InStockSelector,
    string? OutOfStockText,
    bool? RightProduct,
    string Notes,
    PageResult? Result,
    BuyRules.Verdict? Verdict,
    string? Error)
{
    /// <summary>The check with these settings found a price and whether it's in stock.</summary>
    public bool Works => Result is { Price: not null, InStock: not null };
}

/// <summary>
/// ChatGPT sets up a page from its link: the store's name, how to read price and stock (the page's structured data when
/// it has it, otherwise CSS selectors it picks from the rendered page), whether a browser is needed, and whether it's
/// really the product. Every suggestion is checked before it's offered. Also re-learns a page that stopped reading.
/// </summary>
public sealed class PageSetup(OpenAiClient ai, PageFetcher fetcher, BrowserPool browsers, SettingsStore settingsStore)
{
    private const string System = """
        You set up a price and stock tracker for one product page of an online store. You get the page's address, what an
        automatic reader found in its structured data, the page's visible text, candidate price elements and buttons (each with
        a CSS selector), and a screenshot. Answer with JSON only:
        {
          "storeName": "short store name, e.g. Best Buy",
          "isRightProduct": true,
          "productNote": "what product the page is for, and whether it's the one wanted",
          "needsBrowser": false,
          "priceSelector": "CSS selector of the element with the product's current price, or null",
          "titleSelector": "CSS selector of the product's name (often h1), or null",
          "inStockSelector": "CSS selector of the enabled add-to-cart / buy button that only exists while it can be bought, or null",
          "outOfStockText": "comma-separated words the page shows when sold out (e.g. Sold out, Coming soon), or null",
          "notes": "one or two sentences for the owner"
        }
        Rules: use only selectors from the lists given, exactly. The price is the current selling price of this product, not a
        crossed-out price, a monthly payment, a protection plan or another product. Set needsBrowser when the structured data
        and the plain page lacked the price or stock (the page builds itself in script) or the store blocked the plain request.
        isRightProduct is false when the page is for a different product than the one wanted (e.g. a PS5 Slim or a controller
        when a PS5 Pro is wanted).
        """;

    // Leaf elements that look like a price, biggest first (the main price is usually the largest).
    private const string PriceScript = """
        () => {
          const unique = sel => { try { return document.querySelectorAll(sel).length === 1; } catch { return false; } };
          const q = v => JSON.stringify(v);
          const sel = el => {
            const tag = el.tagName.toLowerCase();
            if (el.id && unique('#' + CSS.escape(el.id))) return '#' + CSS.escape(el.id);
            for (const a of ['data-testid', 'data-test', 'itemprop', 'data-automation', 'data-qa'])
              if (el.getAttribute(a) && unique(`${tag}[${a}=${q(el.getAttribute(a))}]`)) return `${tag}[${a}=${q(el.getAttribute(a))}]`;
            const classes = [...el.classList].filter(c => !/\d{3,}/.test(c)).slice(0, 3);
            for (let i = classes.length; i > 0; i--) {
              const s = tag + '.' + classes.slice(0, i).map(c => CSS.escape(c)).join('.');
              if (unique(s)) return s;
            }
            const parts = []; let n = el;
            while (n && n.nodeType === 1 && parts.length < 6) {
              if (n !== el && n.id && unique('#' + CSS.escape(n.id))) { parts.unshift('#' + CSS.escape(n.id)); break; }
              const p = n.parentElement; const same = p ? [...p.children].filter(c => c.tagName === n.tagName) : [];
              parts.unshift(n.tagName.toLowerCase() + (same.length > 1 ? `:nth-of-type(${same.indexOf(n) + 1})` : '')); n = p;
            }
            return parts.join(' > ');
          };
          const re = /(\$|€|£|CA\$|C\$|CAD|USD)\s?\d|\d[\d,.\s]*\d\s?(\$|€|£|CAD|USD)/;
          const out = [];
          for (const el of document.querySelectorAll('body *')) {
            if (el.children.length > 2) continue;
            const t = (el.innerText || '').trim();
            if (!t || t.length > 40 || !re.test(t)) continue;
            const r = el.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            out.push({ selector: sel(el), text: t.replace(/\s+/g, ' '), size: parseFloat(getComputedStyle(el).fontSize) || 0,
              struck: getComputedStyle(el).textDecorationLine.includes('line-through'), top: Math.round(r.top) });
          }
          out.sort((a, b) => b.size - a.size || a.top - b.top);
          const h1 = document.querySelector('h1');
          return { prices: out.slice(0, 25), h1: h1 ? { selector: sel(h1), text: h1.innerText.trim().slice(0, 150) } : null };
        }
        """;

    public async Task<SetupSuggestion> SuggestAsync(string url, Product product, List<SiteCookie> cookies, int? siteId, string purpose,
        CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);

        // 1. What the automatic reader finds on its own (Shopify JSON, structured data).
        var auto = new Site { Url = url, Kind = SiteKind.Auto, ProductId = product.Id };
        PageResult? baseline = null;
        string? httpError = null;
        try
        {
            baseline = await fetcher.FetchAsync(auto, cookies, settings, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            httpError = e.Message.Split('\n')[0];
        }

        // 2. The page in a browser: text, candidate prices, buttons, screenshot.
        PageState state;
        JsonElement extra;
        await using (var context = await browsers.NewContextAsync(settings.UserAgent, cookies, ct))
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8000 });
            }
            catch (TimeoutException)
            {
            }
            state = await PageReader.ReadAsync(page, screenshot: true, ct);
            extra = await page.EvaluateAsync<JsonElement>(PriceScript);
        }

        var q = new StringBuilder()
            .AppendLine($"Wanted product: {product.Name} (title must match: {product.RequiredKeywords}; must not contain: {product.ExcludedKeywords}).")
            .AppendLine($"Page: {url}")
            .AppendLine(httpError != null
                ? $"Plain request (no browser) failed: {httpError}"
                : $"Plain request read: price {baseline?.Price?.ToString() ?? "none"}, in stock {baseline?.InStock?.ToString() ?? "unknown"}, title \"{baseline?.Title}\" ({string.Join(", ", baseline?.Notes ?? [])})")
            .AppendLine()
            .AppendLine("Candidate price elements (biggest first): ");
        foreach (var p in extra.GetProperty("prices").EnumerateArray())
            q.AppendLine($"- selector={p.GetProperty("selector").GetString()} text=\"{p.GetProperty("text").GetString()}\" size={p.GetProperty("size").GetDouble():0}{(p.GetProperty("struck").GetBoolean() ? " (crossed out)" : "")}");
        if (extra.GetProperty("h1") is { ValueKind: JsonValueKind.Object } h1)
            q.AppendLine($"Heading: selector={h1.GetProperty("selector").GetString()} text=\"{h1.GetProperty("text").GetString()}\"");
        q.AppendLine().AppendLine(state.Describe(3000));

        var parts = new List<object> { OpenAiClient.Text(q.ToString()) };
        if (state.Screenshot != null)
            parts.Add(OpenAiClient.Image(state.Screenshot));
        var answer = await ai.AskJsonAsync(purpose, siteId, null, System, parts, ct);

        string? S(string name) => answer.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
        var rightProduct = answer.TryGetProperty("isRightProduct", out var rp) && rp.ValueKind is JsonValueKind.True or JsonValueKind.False ? rp.GetBoolean() : (bool?)null;
        var needsBrowser = answer.TryGetProperty("needsBrowser", out var nb) && nb.ValueKind == JsonValueKind.True;
        var notes = string.Join(" ", new[] { S("productNote"), S("notes") }.Where(x => x != null));

        // 3. Settings to try, best first: structured data alone if it already works; otherwise the AI's selectors, plain
        // then in a browser.
        var candidates = new List<Site>();
        if (baseline is { Price: not null, InStock: not null })
            candidates.Add(new Site { Url = url, Kind = SiteKind.Auto });
        var withSelectors = new Site
        {
            Url = url,
            PriceSelector = S("priceSelector"),
            TitleSelector = S("titleSelector"),
            InStockSelector = S("inStockSelector"),
            OutOfStockText = S("outOfStockText"),
        };
        if (!needsBrowser && httpError == null)
            candidates.Add(Copy(withSelectors, SiteKind.Html));
        candidates.Add(Copy(withSelectors, SiteKind.Browser));

        SetupSuggestion? best = null;
        foreach (var c in candidates)
        {
            c.ProductId = product.Id;
            PageResult? result = null;
            string? error = null;
            try
            {
                result = await fetcher.FetchAsync(c, cookies, settings, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                error = e.Message.Split('\n')[0];
            }
            var suggestion = new SetupSuggestion(S("storeName"), c.Kind, c.PriceSelector, c.TitleSelector, c.InStockSelector, c.OutOfStockText,
                rightProduct, notes, result, result == null ? null : BuyRules.Evaluate(product, c, result), error);
            best ??= suggestion;
            if (suggestion.Works)
                return suggestion;
        }
        return best!;
    }

    private static Site Copy(Site s, SiteKind kind) => new()
    {
        Url = s.Url,
        Kind = kind,
        PriceSelector = s.PriceSelector,
        TitleSelector = s.TitleSelector,
        InStockSelector = s.InStockSelector,
        OutOfStockText = s.OutOfStockText,
    };
}

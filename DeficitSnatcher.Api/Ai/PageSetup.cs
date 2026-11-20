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
    string? Error,
    /// <summary>How to read online and pickup stock separately, when the page shows both.</summary>
    ChannelRules? Channels = null)
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
          "online": { "container": "CSS selector of the element showing whether it can be delivered / shipped / bought online",
                      "inStockText": "comma-separated words it shows when it can (e.g. Available to ship, In stock online)",
                      "outOfStockText": "comma-separated words it shows when it can't (e.g. Sold out online, Not available)" } or null,
          "pickup": { "container": "CSS selector of the element showing whether it can be picked up in a store",
                      "inStockText": "words shown when it can (e.g. Available for pickup, Ready today)",
                      "outOfStockText": "words shown when it can't (e.g. Not available nearby, Sold out at all locations)",
                      "click": "CSS selector of a button / tab / option to click first when pickup only shows after choosing it, else null" } or null,
          "notes": "one or two sentences for the owner"
        }
        Rules: use only selectors from the lists given, exactly. The price is the current selling price of this product, not a
        crossed-out price, a monthly payment, a protection plan or another product. Set needsBrowser when the structured data
        and the plain page lacked the price or stock (the page builds itself in script) or the store blocked the plain request.
        isRightProduct is false when the page is for a different product than the one wanted (e.g. a PS5 Slim or a controller
        when a PS5 Pro is wanted).
        Many stores show two ways to get it: delivered (online) and picked up at a store. When the page shows them separately,
        give "online" and "pickup" from the availability elements listed (their selectors exactly, and words that appear on the
        page or that it would show in the other state), and "click" from the pickup options listed when pickup only shows after
        choosing it. Leave either null when the page doesn't show it.
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
        var auto = new Site { Url = url, Kind = SiteKind.Auto, ProductId = product.Id, Id = siteId ?? 0, Name = new Uri(url).Host };
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

        // 1b. A store with built-in settings (Amazon): those, when they read the page (no AI needed).
        if (StorePresets.For(url) is { } preset)
        {
            foreach (var kind in new[] { preset.Kind, SiteKind.Browser })
            {
                var site = new Site
                {
                    Url = url, Kind = kind, ProductId = product.Id, Id = siteId ?? 0, Name = preset.Name,
                    PriceSelector = preset.Price, TitleSelector = preset.Title, InStockSelector = preset.InStock, OutOfStockText = preset.OutOfStockText,
                };
                try
                {
                    var r = await fetcher.FetchAsync(site, cookies, settings, ct);
                    var s = new SetupSuggestion(preset.Name, kind, preset.Price, preset.Title, preset.InStock, preset.OutOfStockText,
                        r.Title == null ? null : BuyRules.TitleMatches(product, r.Title, out _), preset.Notes, r, BuyRules.Evaluate(product, site, r), null);
                    // Out of stock is a fine answer too: the price may simply not be shown then.
                    if (r.InStock != null && (r.Price != null || r.InStock == false))
                        return s with { RightProduct = s.RightProduct };
                }
                catch (BlockedException e)
                {
                    return new SetupSuggestion(preset.Name, kind, preset.Price, preset.Title, preset.InStock, preset.OutOfStockText, null,
                        preset.Notes, null, null, e.Message);
                }
                catch (HttpRequestException)
                {
                    // Try the browser next.
                }
            }
        }

        // 2. The page in a browser: text, candidate prices, buttons, screenshot.
        PageState state;
        JsonElement extra;
        JsonElement availability;
        await using (var lease = await browsers.OpenAsync(settings, cookies, ct, siteId is { } sid ? new Site { Id = sid, Url = url } : null))
        {
            var page = await lease.NewPageAsync();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8000 });
            }
            catch (TimeoutException)
            {
            }
            state = await PageReader.ReadAsync(page, screenshot: true, ct);
            // Blocked in the browser too: there's nothing for the AI to read, so don't ask it.
            if (BlockDetector.Detect(await page.ContentAsync()) is { } vendor)
                return new SetupSuggestion(new Uri(url).Host.Replace("www.", ""), SiteKind.Browser, null, null, null, null, null,
                    BlockDetector.Message(vendor, browser: true), null, null, BlockDetector.Message(vendor, browser: true));
            extra = await page.EvaluateAsync<JsonElement>(PriceScript);
            availability = await page.EvaluateAsync<JsonElement>(AvailabilityScript);
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
        q.AppendLine().AppendLine("Availability elements (delivery / pickup / stock wording):");
        foreach (var a in availability.GetProperty("blocks").EnumerateArray())
            q.AppendLine($"- selector={a.GetProperty("selector").GetString()} text=\"{a.GetProperty("text").GetString()}\"");
        q.AppendLine("Pickup options that can be clicked:");
        foreach (var a in availability.GetProperty("options").EnumerateArray())
            q.AppendLine($"- selector={a.GetProperty("selector").GetString()} text=\"{a.GetProperty("text").GetString()}\"");
        q.AppendLine().AppendLine(state.Describe(3000));

        var parts = new List<object> { OpenAiClient.Text(q.ToString()) };
        if (state.Screenshot != null)
            parts.Add(OpenAiClient.Image(state.Screenshot, detail: "low"));
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
            StockChannels = StockChannels.Serialize(new ChannelRules(Rule(answer, "online"), Rule(answer, "pickup"))),
        };
        if (!needsBrowser && httpError == null)
            candidates.Add(Copy(withSelectors, SiteKind.Html));
        candidates.Add(Copy(withSelectors, SiteKind.Browser));

        SetupSuggestion? best = null;
        foreach (var c in candidates)
        {
            c.ProductId = product.Id;
            c.Id = siteId ?? 0;
            c.Name = new Uri(url).Host;
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
                rightProduct, notes, result, result == null ? null : BuyRules.Evaluate(product, c, result), error, StockChannels.Parse(c.StockChannels));
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
        StockChannels = s.StockChannels,
    };

    /// <summary>The AI's online or pickup rule, or null.</summary>
    private static ChannelRule? Rule(JsonElement answer, string name)
    {
        if (!answer.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.Object)
            return null;
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
        return S("container") is { } container ? new ChannelRule(container, S("inStockText"), S("outOfStockText"), name == "pickup" ? S("click") : null) : null;
    }

    // Elements that say how it can be had (shipping, delivery, pickup, stock), smallest first, and pickup options to click.
    // Shares the selector-making code with PriceScript.
    private static readonly string AvailabilityScript = "() => {\n"
        + PriceScript[PriceScript.IndexOf("const unique", StringComparison.Ordinal)..PriceScript.IndexOf("const re =", StringComparison.Ordinal)]
        + """
          const words = /(ship|deliver|online|pick ?up|in[- ]store|at (all )?(locations|stores)|sold out|in stock|out of stock|unavailable|available|coming soon|ready)/i;
          const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const blocks = [];
          for (const el of document.querySelectorAll('body *')) {
            if (['SCRIPT', 'STYLE', 'NOSCRIPT', 'SVG'].includes(el.tagName) || !visible(el)) continue;
            const t = (el.innerText || '').trim().replace(/\s+/g, ' ');
            if (t.length < 4 || t.length > 160 || !words.test(t) || el.querySelectorAll('*').length > 30) continue;
            blocks.push({ el, selector: sel(el), text: t });
          }
          // Keep the outermost of nested matches with the same text, and drop near-duplicates.
          const seen = new Set(); const kept = [];
          for (const b of blocks.sort((a, b) => b.text.length - a.text.length)) {
            if (seen.has(b.text)) continue; seen.add(b.text); kept.push(b);
          }
          const options = [];
          for (const el of document.querySelectorAll('button, [role=tab], [role=radio], label, input[type=radio], a')) {
            const t = (el.innerText || el.getAttribute('aria-label') || el.value || '').trim().replace(/\s+/g, ' ');
            if (t && t.length < 80 && /pick ?up|in[- ]store|store/i.test(t) && !/preferred|locator|find a store|store hours|my store/i.test(t) && visible(el)) options.push({ selector: sel(el), text: t });
          }
          return { blocks: kept.slice(0, 30).map(b => ({ selector: b.selector, text: b.text })), options: options.slice(0, 10) };
        }
        """;
}

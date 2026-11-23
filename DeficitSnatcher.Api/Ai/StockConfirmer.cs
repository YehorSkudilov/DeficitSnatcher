using System.Text.Json;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Ai;

/// <summary>
/// The last word before an alarm or a buy: the page is opened fresh in the browser and looked at as a person would, not
/// read from its hidden data or rules. The AI gets a screenshot and the visible text of the product's own area and says
/// whether this product can be bought right now (online or pickup). Without the AI, sold-out wording in that visible area
/// is enough to say no. Only called when a page has just become a match, which is rare.
/// </summary>
public sealed class StockConfirmer(BrowserPool browsers, OpenAiClient ai, ILogger<StockConfirmer> log)
{
    /// <summary>Yes, no, or null when the page couldn't be looked at (blocked, failed).</summary>
    public sealed record Confirmation(bool? Buyable, string Why);

    private const string System = """
        You double-check a stock alert before it rings the owner's phone or buys. You get a screenshot of a store's product
        page and the visible text of the product's own area. Answer with JSON only:
        { "buyable": true or false, "online": true/false/null, "pickup": true/false/null, "reason": "a few words" }
        buyable is true only when THIS product (not a recommended or related one) can be added to the cart or bought now,
        delivered or picked up: an enabled Add to cart / Buy now / Pick up button, or "In stock" / "Available" for it.
        Some stores (Staples) show an Add to cart button even when it can't be delivered or picked up: then the delivery and
        pickup wording decides, and "Delivery currently unavailable" with no store pickup is false.
        It is false when the page says Out of stock, Sold out, Unavailable, Coming soon, Notify me, Pre-order closed, the
        buy button is missing or disabled, or the page isn't the product (an error, a search page, a captcha).
        When unsure, false.
        """;

    // The product's own area: from the main heading up to the first ancestor that also holds a price, so menus, footers
    // and other products' tiles are left out. The whole visible page when there's no heading.
    private const string MainText = """
        () => {
          const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const h1 = [...document.querySelectorAll('h1')].find(vis);
          const price = /(\$|CA\$|C\$|CAD)\s?\d/;
          let el = h1;
          while (el && el !== document.body && !(price.test(el.innerText || '') && (el.innerText || '').length > 200)) el = el.parentElement;
          const text = ((el && el !== document.body ? el : document.body).innerText || '').replace(/\n{3,}/g, '\n\n');
          return text.slice(0, 4000);
        }
        """;

    // A visible, enabled "Add to cart" / "Buy now" button, in the product's area when it can be found: what a person would
    // need to see to buy it. Its text, or null when there's none.
    private const string BuyButton = """
        () => {
          const vis = el => { const r = el.getBoundingClientRect(); const st = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && st.visibility !== 'hidden' && st.display !== 'none' && Number(st.opacity) > 0.3; };
          const words = /add\s*to\s*(cart|bag|basket)|buy\s*(it\s*)?now|add\s*for\s*(delivery|shipping)/i;
          const h1 = [...document.querySelectorAll('h1')].find(vis);
          // From the heading up to the nearest ancestor holding a buy button (not past the main content); no heading: the page.
          let scope = h1 ? null : document;
          for (let area = h1, i = 0; area && i < 8; i++) {
            area = area.parentElement;
            if (!area || area === document.body || area.matches('main, [role=main]')) break;
            if ([...area.querySelectorAll('button, input[type=submit], a[role=button]')].some(b => words.test(b.innerText || b.value || ''))) { scope = area; break; }
          }
          if (!scope) return null;
          const off = b => b.disabled || b.getAttribute('aria-disabled') === 'true' || /(^|[\s_-])disabled/i.test(b.className || '');
          const b = [...scope.querySelectorAll('button, input[type=submit], a[role=button]')]
            .find(b => words.test(b.innerText || b.value || '') && vis(b) && !off(b));
          return b ? (b.innerText || b.value || '').trim().slice(0, 60) : null;
        }
        """;

    // The AI's last "no" per page: while the page still shows sold-out wording, it isn't asked again for 30 minutes (a page
    // that keeps misreading as in stock would otherwise cost an AI call every check).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTimeOffset At, string Why)> _lastNo = new();

    private static readonly string[] SoldOut =
        ["out of stock", "sold out", "unavailable", "not available", "coming soon", "notify me", "back order", "backorder", "no longer available"];

    public async Task<Confirmation> ConfirmAsync(Site site, Product product, List<SiteCookie> cookies, BotSettings settings, CancellationToken ct)
    {
        string main;
        byte[] shot;
        string? buyButton;
        bool? channels = null;
        string? channelDetail = null;
        try
        {
            await using var lease = await browsers.OpenAsync(settings, cookies, ct, site);
            var page = await lease.NewPageAsync();
            await page.GotoAsync(site.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = settings.RequestTimeoutSeconds * 1000 });
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8000 });
            }
            catch (TimeoutException)
            {
            }
            // Stock messages drawn late (Staples' "Out of Stock").
            await page.WaitForTimeoutAsync(3000);
            var html = await page.ContentAsync();
            if (BlockDetector.Detect(html, 200) is { } vendor)
                return new(null, $"blocked by {vendor}");
            // The page's own delivery / pickup boxes, read again on this fresh copy: they must still say available.
            var rules = StockChannels.For(site.StockChannels, new Uri(site.Url));
            if (rules?.Online is { IsEmpty: false } online)
            {
                try
                {
                    await page.Locator(online.Container).First.WaitForAsync(new() { Timeout = 8000 });
                }
                catch (Exception e) when (e is PlaywrightException or TimeoutException)
                {
                }
                // Staples draws "Delivers by…" first and only later swaps in "Delivery currently unavailable": the box is
                // watched for about 10 seconds, and any sold-out reading, or none saying available at the end, is a no.
                var texts = new List<string>();
                for (var i = 0; i < 4; i++)
                {
                    if (i > 0)
                        await page.WaitForTimeoutAsync(3000);
                    try
                    {
                        html = await page.ContentAsync();
                    }
                    catch (PlaywrightException)
                    {
                        continue;
                    }
                    var (said, text) = StockChannels.ReadOne(html, online);
                    if (text != null && (texts.Count == 0 || texts[^1] != text))
                        texts.Add(text.Length > 160 ? text[..160] + "…" : text);
                    if (said == false)
                    {
                        channels = false;
                        break;
                    }
                    channels = said;
                }
                channelDetail = texts.Count == 0 ? "the delivery box wasn't found" : "the delivery box said " + string.Join(" → ", texts.Select(t => $"\"{t}\""));
            }
            main = await page.EvaluateAsync<string>(MainText);
            buyButton = await page.EvaluateAsync<string?>(BuyButton);
            shot = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 60 });
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is PlaywrightException or TimeoutException or BrowserEngineException)
        {
            return new(null, $"the page didn't open ({e.Message.Split('\n')[0]})");
        }

        // The delivery box was read as available in the check, and now (looked at fresh, after it's drawn) it says it isn't.
        // The check went by this box: looked at fresh for 10 s, it must say available at the end, not sold out or nothing.
        if (channelDetail != null && channels != true)
            return new(false, $"looked at again for 10 s, {channelDetail}");

        var soldOutWord = SoldOut.FirstOrDefault(w => main.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (soldOutWord != null && _lastNo.TryGetValue(site.Id, out var no) && DateTimeOffset.UtcNow - no.At < TimeSpan.FromMinutes(30))
            return new(false, $"the page still shows \"{soldOutWord}\" ({no.Why}, {(int)(DateTimeOffset.UtcNow - no.At).TotalMinutes} min ago)");
        var noAi = "the AI is off";
        if (await ai.AvailableAsync(ct))
            try
            {
                var answer = await ai.AskJsonAsync("confirm-stock", site.Id, null, System,
                [
                    OpenAiClient.Text($"Product wanted: {product.Name}. Page: {site.Url}\n"
                        + $"Enabled buy button found: {buyButton ?? "none"}. Delivery: {channelDetail ?? "not read"}.\n\n"
                        + $"Visible text of the product's area:\n{main}"),
                    OpenAiClient.Image(shot, "image/jpeg", detail: "high"),
                ], ct, ignoreDailyLimit: true);
                var reason = answer.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                if (answer.TryGetProperty("buyable", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    var why = $"the AI looked at the page: {reason}";
                    if (b.ValueKind == JsonValueKind.False)
                        _lastNo[site.Id] = (DateTimeOffset.UtcNow, why);
                    else
                        _lastNo.TryRemove(site.Id, out _);
                    return new(b.ValueKind == JsonValueKind.True, why + (channelDetail == null ? "" : $"; {channelDetail}"));
                }
            }
            catch (OpenAiClient.AiException e)
            {
                log.LogInformation("Stock confirmation for {Site}: the AI couldn't answer ({Error}); using the page's words", site.Name, e.Message);
                noAi = "the AI couldn't answer: " + e.Message;
            }
        // Without the AI: no sold-out words isn't enough (Staples rang that way while its page said Out of Stock); a person
        // would need an enabled buy button for this product.
        if (soldOutWord != null)
            return new(false, $"the page shows \"{soldOutWord}\" ({noAi})");
        return buyButton != null
            ? new(true, $"an enabled \"{buyButton}\" button and no sold-out wording ({noAi})")
            : new(false, $"no enabled Add to cart / Buy now button on the page ({noAi})");
    }
}

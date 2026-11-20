using System.Net;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>Fetches a site's product page and reads it (see PageParser), the way the site's kind says.</summary>
public sealed class PageFetcher(HttpClient http, BrowserPool browsers, SettingsStore settingsStore, ILogger<PageFetcher> log)
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    // Auto-kind pages whose plain request was blocked: checked in the browser for a day before trying plain again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTimeOffset> NeedsBrowser = new();

    public async Task<PageResult> FetchAsync(Site site, List<SiteCookie> cookies, BotSettings settings, CancellationToken ct)
    {
        var url = new Uri(site.Url);
        if (site.Kind == SiteKind.Shopify || site.Kind == SiteKind.Auto && LooksLikeShopifyProduct(url))
        {
            var jsonUrl = new UriBuilder(url) { Query = "", Fragment = "" }.Uri.ToString().TrimEnd('/') + ".js";
            var (status, body, retryAfter) = await GetAsync(new Uri(jsonUrl), cookies, settings, "application/json", ct);
            if (BlockDetector.Detect(body, (int)status) is { } jsonBlock)
                throw new BlockedException($"The site answered {(int)status}: " + BlockDetector.Message(jsonBlock, browser: false), retryAfter);
            if (status == HttpStatusCode.OK && PageParser.ParseShopify(body, VariantFromUrl(url) ?? site.VariantId) is { } shopify)
                return shopify with { Source = body, SourceType = "application/json" };
            if (site.Kind == SiteKind.Shopify)
                throw new HttpRequestException($"Shopify product JSON answered {(int)status}; is this a /products/… page of a Shopify store?");
        }

        var selectors = new PageParser.Selectors(site.PriceSelector, site.TitleSelector, site.InStockSelector, site.OutOfStockText);
        var useBrowser = site.Kind == SiteKind.Browser
            || site.Kind == SiteKind.Auto && NeedsBrowser.TryGetValue(site.Id, out var since) && DateTimeOffset.UtcNow - since < TimeSpan.FromDays(1);
        if (useBrowser)
        {
            var (rendered, engine, status, retryAfter, extra) = await RenderAsync(url, cookies, settings, ct, site);
            if (BlockDetector.Detect(rendered, status ?? 200) is { } vendor)
                throw new BlockedException($"In {BrowserPool.EngineName(engine)}: " + BlockDetector.Message(vendor, browser: true), retryAfter);
            return WithExtra(StoreLabels.Apply(PageParser.ParseHtml(rendered, selectors), rendered, url) with { Source = rendered, SourceType = "text/html" }, url, extra);
        }

        // A throwaway parameter, as StreetMerchant does, so a cache in front of the store can't answer with an old copy.
        var fresh = new UriBuilder(url) { Query = (url.Query.Length > 1 ? url.Query[1..] + "&" : "") + "_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.Uri;
        var (code, html, httpRetryAfter) = await GetAsync(fresh, cookies, settings, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", ct);
        if (BlockDetector.Detect(html, (int)code) is { } blockedBy && site.Kind == SiteKind.Auto)
        {
            // Bot protection turns plain requests away: the same check, at once, in the browser (which runs its scripts and
            // challenges), and the page's next checks go there too.
            log.LogInformation("Plain request to {Site} blocked by {Vendor}; checking in the browser instead", site.Name, blockedBy);
            NeedsBrowser[site.Id] = DateTimeOffset.UtcNow;
            var (rendered, engine, status, retryAfter, extra) = await RenderAsync(url, cookies, settings, ct, site);
            if (BlockDetector.Detect(rendered, status ?? 200) is { } vendor)
                throw new BlockedException($"In {BrowserPool.EngineName(engine)}: " + BlockDetector.Message(vendor, browser: true), retryAfter);
            return WithExtra(StoreLabels.Apply(PageParser.ParseHtml(rendered, selectors), rendered, url) with { Source = rendered, SourceType = "text/html" }, url, extra);
        }
        if (BlockDetector.Detect(html, (int)code) is { } blockedBy2)
            throw new BlockedException($"The site answered {(int)code}: " + BlockDetector.Message(blockedBy2, browser: false)
                + " Or try the Browser kind.", httpRetryAfter);
        if (code != HttpStatusCode.OK)
            throw new HttpRequestException($"The site answered {(int)code} {code}.");
        return StoreLabels.Apply(PageParser.ParseHtml(html, selectors), html, url) with { Source = html, SourceType = "text/html" };
    }

    private async Task<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)> GetAsync(Uri url, List<SiteCookie> cookies,
        BotSettings settings, string accept, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", settings.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-CA,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        var cookieHeader = SiteCookies.Header(cookies, url);
        if (cookieHeader.Length > 0)
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        var client = StoreProxy.Parse(settings.ProxyUrl) is { } proxy ? StoreProxy.Client(proxy) : http;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        return (response.StatusCode, body, RetryAfter(response.Headers.RetryAfter));
    }

    private async Task<(string Html, string Engine, int? Status, TimeSpan? RetryAfter, string? Extra)> RenderAsync(Uri url,
        List<SiteCookie> cookies, BotSettings settings, CancellationToken ct, Site site)
    {
        try
        {
            return await RenderOnceAsync(url, cookies, settings, ct, site);
        }
        catch (Microsoft.Playwright.PlaywrightException e) when (!ct.IsCancellationRequested && e.Message.Contains("has been closed"))
        {
            // The browser restarted or dropped the tab under us: not the store's doing, so once more on a fresh one.
            return await RenderOnceAsync(url, cookies, settings, ct, site);
        }
    }

    private async Task<(string Html, string Engine, int? Status, TimeSpan? RetryAfter, string? Extra)> RenderOnceAsync(Uri url,
        List<SiteCookie> cookies, BotSettings settings, CancellationToken ct, Site site)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        log.LogInformation("Browser check for {Site} waiting for a browser context", site.Name);
        await using var lease = await browsers.OpenAsync(settings, cookies, ct, site);
        log.LogInformation("Browser check for {Site} opened {Engine} context in {ElapsedMs} ms", site.Name, lease.Engine, watch.ElapsedMilliseconds);
        var page = await lease.NewPageAsync();
        // StreetMerchant's low-bandwidth mode skips images and fonts. A stock check only needs the document and its
        // scripts/styles, so avoid downloading these large assets on checks (checkout pages keep their full resources).
        await page.RouteAsync("**/*", async route =>
        {
            var type = route.Request.ResourceType;
            if (type is "image" or "font" or "media")
                await route.AbortAsync();
            else
                await route.ContinueAsync();
        });
        if (lease.Fresh)
        {
            // A new session: the store's home page first, as a person arriving would (StreetMerchant's "captcha
            // deterrent"), so the bot check's scripts run and set their cookies before the product page is asked for.
            try
            {
                log.LogInformation("Browser check for {Site}: new session, visiting the home page first", site.Name);
                await page.GotoAsync(url.GetLeftPart(UriPartial.Authority) + "/", new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                await Task.Delay(TimeSpan.FromMilliseconds(2000 + Random.Shared.Next(2000)), ct);
            }
            catch (Exception e) when (e is Microsoft.Playwright.PlaywrightException or TimeoutException)
            {
                log.LogInformation("Browser check for {Site}: home page visit failed ({Error}); going straight to the product", site.Name, e.Message.Split('\n')[0]);
            }
        }
        log.LogInformation("Browser check for {Site} navigating to the product page", site.Name);
        var response = await page.GotoAsync(url.ToString(), new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded, Timeout = settings.RequestTimeoutSeconds * 1000 });
        log.LogInformation("Browser check for {Site} navigation finished with HTTP {Status} in {ElapsedMs} ms",
            site.Name, response?.Status, watch.ElapsedMilliseconds);
        try
        {
            await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle, new() { Timeout = 8000 });
            log.LogInformation("Browser check for {Site} reached network idle after {ElapsedMs} ms", site.Name, watch.ElapsedMilliseconds);
        }
        catch (TimeoutException)
        {
            // Pages that keep polling never go idle; what's there by now is enough.
            log.LogInformation("Browser check for {Site} continued after the network-idle wait timed out", site.Name);
        }
        var retryAfter = response?.Headers.TryGetValue("retry-after", out var value) == true ? ParseRetryAfter(value) : null;
        var html = await page.ContentAsync();
        int? status = response?.Status;
        if (BlockDetector.IsChallenge(html))
        {
            // Cloudflare's "Just a moment…" often clears by itself in a real browser after a few seconds, then the store's
            // page loads. Nothing is clicked: one that asks for a person (a checkbox) stays a block.
            log.LogInformation("Browser check for {Site} got a Cloudflare challenge; waiting for it to clear", site.Name);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline && BlockDetector.IsChallenge(html))
            {
                await Task.Delay(2000, ct);
                try
                {
                    html = await page.ContentAsync();
                }
                catch (Microsoft.Playwright.PlaywrightException)
                {
                    // Navigating away from the challenge right now: read again next round.
                }
            }
            if (!BlockDetector.IsChallenge(html))
            {
                status = 200;
                try
                {
                    await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle, new() { Timeout = 8000 });
                }
                catch (TimeoutException)
                {
                }
                html = await page.ContentAsync();
                log.LogInformation("Browser check for {Site} got past the challenge in {ElapsedMs} ms", site.Name, watch.ElapsedMilliseconds);
            }
        }
        log.LogInformation("Browser check for {Site} read {Length} characters of page content in {ElapsedMs} ms",
            site.Name, html.Length, watch.ElapsedMilliseconds);
        // Best Buy: online and pickup, from its own stock lookup (asked from this tab, past the same bot check).
        string? extra = null;
        if (BestBuyAvailability.Applies(url) && BestBuyAvailability.Sku(url) is { } sku && BlockDetector.Detect(html, status ?? 200) == null)
        {
            var profile = await settingsStore.GetProfileAsync(ct);
            extra = await BestBuyAvailability.FetchAsync(page, sku, profile.PostalCode, log);
            log.LogInformation("Browser check for {Site}: Best Buy availability lookup {Outcome}", site.Name, extra == null ? "failed" : "answered");
        }
        return (html, lease.Engine, status, retryAfter, extra);
    }

    /// <summary>A store's own stock lookup, when there was one (Best Buy), over what the page itself said.</summary>
    private static PageResult WithExtra(PageResult result, Uri url, string? extra) =>
        extra != null && BestBuyAvailability.Applies(url) ? BestBuyAvailability.Apply(result, extra)
        : BestBuyAvailability.Applies(url) ? result with { Notes = [.. result.Notes, "best buy lookup: not answered; stock from the page"] }
        : result;

    private static TimeSpan? RetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? header) =>
        header?.Delta is { } delta ? delta : header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;

    private static TimeSpan? ParseRetryAfter(string? value)
    {
        if (int.TryParse(value, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return DateTimeOffset.TryParse(value, out var date) ? date - DateTimeOffset.UtcNow : null;
    }

    private static bool LooksLikeShopifyProduct(Uri url) => url.AbsolutePath.Contains("/products/", StringComparison.OrdinalIgnoreCase);

    private static string? VariantFromUrl(Uri url) =>
        System.Web.HttpUtility.ParseQueryString(url.Query)["variant"] is { Length: > 0 } v ? v : null;
}

/// <summary>The store's bot protection answered instead of the page.</summary>
public sealed class BlockedException(string message, TimeSpan? retryAfter = null) : HttpRequestException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter is { } delay && delay > TimeSpan.Zero ? delay : null;
}

using System.Net;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>Fetches a site's product page and reads it (see PageParser), the way the site's kind says.</summary>
public sealed class PageFetcher(HttpClient http, BrowserPool browsers, Pc.HumanCheck humanCheck, Pc.WorkerPool workers, RemoteRender remote)
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <param name="worker">Who reads it: "server", or "pc:{id}" for a PC helper (see Pc/WorkerPool); null: a helper for
    /// a Run-on-my-PC page when one's connected, else the server.</param>
    public async Task<PageResult> FetchAsync(Site site, List<SiteCookie> cookies, BotSettings settings, CancellationToken ct, string? worker = null)
    {
        var url = new Uri(site.Url);
        var selectors0 = new PageParser.Selectors(site.PriceSelector, site.TitleSelector, site.InStockSelector, site.OutOfStockText);
        worker ??= site.RunOnPc ? workers.Best(site) : Pc.WorkerPool.Server;
        // On a PC helper everything goes through its Chrome (its home connection and sign-ins). A helper that's gone
        // falls back to the server, like any other page.
        if (Pc.WorkerPool.PcId(worker) != null && workers.Available(worker))
        {
            var (rendered, ranOn) = await RenderAsync(url, cookies, settings, ct, site, worker);
            if (BlockDetector.Detect(rendered) is { } pcVendor)
                throw new BlockedException(BlockDetector.Message(pcVendor, browser: true));
            return PageParser.ParseHtml(rendered, selectors0) with { Source = rendered, SourceType = "text/html", Worker = ranOn };
        }
        if (site.Kind == SiteKind.Shopify || site.Kind == SiteKind.Auto && LooksLikeShopifyProduct(url))
        {
            var jsonUrl = new UriBuilder(url) { Query = "", Fragment = "" }.Uri.ToString().TrimEnd('/') + ".js";
            var (status, body) = await GetAsync(new Uri(jsonUrl), cookies, settings, "application/json", ct);
            if (status == HttpStatusCode.OK && PageParser.ParseShopify(body, VariantFromUrl(url) ?? site.VariantId) is { } shopify)
                return shopify with { Source = body, SourceType = "application/json" };
            if (site.Kind == SiteKind.Shopify)
                throw new HttpRequestException($"Shopify product JSON answered {(int)status}; is this a /products/… page of a Shopify store?");
        }

        var selectors = new PageParser.Selectors(site.PriceSelector, site.TitleSelector, site.InStockSelector, site.OutOfStockText);
        if (site.Kind is SiteKind.Unlocker or SiteKind.BrightData or SiteKind.Camoufox)
            return await FetchRemoteAsync(site, url, cookies, settings, selectors, ct);
        if (site.Kind == SiteKind.Browser)
        {
            var (rendered, _) = await RenderAsync(url, cookies, settings, ct, site, Pc.WorkerPool.Server);
            if (BlockDetector.Detect(rendered) is { } vendor)
                throw new BlockedException(BlockDetector.Message(vendor, browser: true));
            return PageParser.ParseHtml(rendered, selectors) with { Source = rendered, SourceType = "text/html" };
        }

        var (code, html) = await GetAsync(url, cookies, settings, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", ct);
        if (BlockDetector.Detect(html, (int)code) is { } blockedBy)
            throw new BlockedException($"The site answered {(int)code}: " + BlockDetector.Message(blockedBy, browser: false)
                + " Or try the Browser kind.");
        if (code != HttpStatusCode.OK)
            throw new HttpRequestException($"The site answered {(int)code} {code}.");
        return PageParser.ParseHtml(html, selectors) with { Source = html, SourceType = "text/html" };
    }

    /// <summary>Through Bright Data or Camoufox (see RemoteRender). A store that still blocks it loses the saved session,
    /// so the next check comes as someone new.</summary>
    private async Task<PageResult> FetchRemoteAsync(Site site, Uri url, List<SiteCookie> cookies, BotSettings settings, PageParser.Selectors selectors,
        CancellationToken ct)
    {
        int status;
        string html, how;
        if (site.Kind == SiteKind.Unlocker)
        {
            var (code, body) = await remote.UnlockerAsync(url, settings, ct);
            (status, html, how) = ((int)code, body, "Web Unlocker");
        }
        else if (site.Kind == SiteKind.BrightData)
            (status, html, how) = (200, await remote.BrightDataAsync(site, url, cookies, settings, ct), "Bright Data's browser");
        else
        {
            var (code, body) = await remote.CamoufoxAsync(site, url, cookies, settings, ct);
            (status, html, how) = (code, body, "Camoufox");
        }
        if (BlockDetector.Detect(html, status) is { } vendor)
        {
            remote.Forget(site.Id);
            throw new BlockedException($"Through {how}: " + BlockDetector.Message(vendor, browser: site.Kind != SiteKind.Unlocker));
        }
        if (status >= 400)
            throw new HttpRequestException($"Through {how}, the site answered {status}.");
        return PageParser.ParseHtml(html, selectors) with { Source = html, SourceType = "text/html" };
    }

    private async Task<(HttpStatusCode, string)> GetAsync(Uri url, List<SiteCookie> cookies, BotSettings settings, string accept, CancellationToken ct)
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
        return (response.StatusCode, body);
    }

    private async Task<(string Html, string Worker)> RenderAsync(Uri url, List<SiteCookie> cookies, BotSettings settings, CancellationToken ct, Site? site,
        string worker)
    {
        // On a helper: the page's own tab, kept open and refreshed each time, like a person leaving it open.
        if (Pc.WorkerPool.PcId(worker) is { } runnerId && site != null && browsers.PcAvailable)
        {
            var gate = browsers.TabGate(runnerId, site.Id);
            await gate.WaitAsync(ct);
            try
            {
                var tab = await browsers.KeptTabAsync(runnerId, site.Id, url.ToString(), settings.RequestTimeoutSeconds * 1000, ct);
                try
                {
                    await tab.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle, new() { Timeout = 8000 });
                }
                catch (TimeoutException)
                {
                }
                await humanCheck.WaitIfBlockedAsync(tab, site.Id, site.Name, ct);
                return (await tab.ContentAsync(), worker);
            }
            catch (PcHelperException)
            {
                // Its Chrome can't be reached: the usual way below (the server, when the helper isn't there).
            }
            finally
            {
                gate.Release();
            }
        }

        await using var lease = await browsers.OpenAsync(settings, cookies, worker, ct);
        var onPc = lease.OnPc;
        var page = await lease.NewPageAsync();
        await page.GotoAsync(url.ToString(), new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded, Timeout = settings.RequestTimeoutSeconds * 1000 });
        try
        {
            await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle, new() { Timeout = 8000 });
        }
        catch (TimeoutException)
        {
            // Pages that keep polling never go idle; what's there by now is enough.
        }
        if (onPc)
            await humanCheck.WaitIfBlockedAsync(page, site!.Id, site.Name, ct);
        return (await page.ContentAsync(), lease.Worker);
    }

    private static bool LooksLikeShopifyProduct(Uri url) => url.AbsolutePath.Contains("/products/", StringComparison.OrdinalIgnoreCase);

    private static string? VariantFromUrl(Uri url) =>
        System.Web.HttpUtility.ParseQueryString(url.Query)["variant"] is { Length: > 0 } v ? v : null;
}

/// <summary>The store's bot protection answered instead of the page.</summary>
public sealed class BlockedException(string message) : HttpRequestException(message);

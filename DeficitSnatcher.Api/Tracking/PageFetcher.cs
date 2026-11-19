using System.Net;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>Fetches a site's product page and reads it (see PageParser), the way the site's kind says.</summary>
public sealed class PageFetcher(HttpClient http, BrowserPool browsers)
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public async Task<PageResult> FetchAsync(Site site, List<SiteCookie> cookies, BotSettings settings, CancellationToken ct)
    {
        var url = new Uri(site.Url);
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
        if (site.Kind == SiteKind.Browser)
        {
            var rendered = await RenderAsync(url, cookies, settings, ct);
            return PageParser.ParseHtml(rendered, selectors) with { Source = rendered, SourceType = "text/html" };
        }

        var (code, html) = await GetAsync(url, cookies, settings, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", ct);
        if (code != HttpStatusCode.OK)
            throw new HttpRequestException(code is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? $"The site answered {(int)code} {code}: it's likely blocking bots. Try the Browser kind, a longer interval, or the site's cookies."
                : $"The site answered {(int)code} {code}.");
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
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        return (response.StatusCode, body);
    }

    private async Task<string> RenderAsync(Uri url, List<SiteCookie> cookies, BotSettings settings, CancellationToken ct)
    {
        await using var context = await browsers.NewContextAsync(settings.UserAgent, cookies, ct);
        var page = await context.NewPageAsync();
        await page.GotoAsync(url.ToString(), new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded, Timeout = settings.RequestTimeoutSeconds * 1000 });
        try
        {
            await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle, new() { Timeout = 8000 });
        }
        catch (TimeoutException)
        {
            // Pages that keep polling never go idle; what's there by now is enough.
        }
        return await page.ContentAsync();
    }

    private static bool LooksLikeShopifyProduct(Uri url) => url.AbsolutePath.Contains("/products/", StringComparison.OrdinalIgnoreCase);

    private static string? VariantFromUrl(Uri url) =>
        System.Web.HttpUtility.ParseQueryString(url.Query)["variant"] is { Length: > 0 } v ? v : null;
}

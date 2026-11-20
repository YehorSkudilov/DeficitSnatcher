using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using Microsoft.Playwright;
using Cookie = Microsoft.Playwright.Cookie;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Pages read somewhere other than the server's own Chromium, for stores that block it:
/// <list type="bullet">
/// <item><b>Unlocker</b>: Bright Data's Web Unlocker API (an HTTP request; they get past the bot check and send back the HTML).</item>
/// <item><b>BrightData</b>: Bright Data's Browser API, a Chrome in their cloud driven over CDP (Playwright's ConnectOverCDP).</item>
/// <item><b>Camoufox</b>: the Camoufox service (DeficitSnatcher.Camoufox: a Firefox that hides automation), over HTTP.</item>
/// </list>
/// The two browsers keep each page's session between checks: its cookies and local storage are saved after every check
/// and put back before the next one, so the store sees one returning visitor rather than a stranger every minute. A saved
/// session is dropped when it's older than Settings → Remote session minutes, or when the store blocked it.
/// </summary>
public sealed class RemoteRender(IHttpClientFactory clients, ILogger<RemoteRender> log)
{
    public const string ClientName = "remote-render";
    private const string UnlockerEndpoint = "https://api.brightdata.com/request";

    private sealed record Saved(string State, DateTimeOffset At);
    private readonly ConcurrentDictionary<(SiteKind, int), Saved> _sessions = new();
    private IPlaywright? _playwright;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The page's saved session is forgotten: the next check starts as a new visitor.</summary>
    public void Forget(int siteId)
    {
        foreach (var key in _sessions.Keys.Where(k => k.Item2 == siteId).ToList())
            _sessions.TryRemove(key, out _);
    }

    private string? SessionFor(SiteKind kind, int siteId, BotSettings settings)
    {
        if (siteId == 0 || settings.RemoteSessionMinutes <= 0 || !_sessions.TryGetValue((kind, siteId), out var saved))
            return null;
        if (DateTimeOffset.UtcNow - saved.At <= TimeSpan.FromMinutes(settings.RemoteSessionMinutes))
            return saved.State;
        _sessions.TryRemove((kind, siteId), out _);
        log.LogInformation("Saved {Kind} session of site {Site} expired; starting a new one", kind, siteId);
        return null;
    }

    private void Save(SiteKind kind, int siteId, BotSettings settings, string? state)
    {
        if (siteId != 0 && settings.RemoteSessionMinutes > 0 && !string.IsNullOrEmpty(state))
            _sessions[(kind, siteId)] = new Saved(state, DateTimeOffset.UtcNow);
    }

    // ---- Bright Data Web Unlocker ----

    /// <summary>The page's HTML through the Web Unlocker API, and the status the store answered with.</summary>
    public async Task<(HttpStatusCode Status, string Html)> UnlockerAsync(Uri url, BotSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.BrightDataApiToken) || string.IsNullOrWhiteSpace(settings.BrightDataUnlockerZone))
            throw new HttpRequestException("Web Unlocker isn't set up: add the Bright Data API token and Web Unlocker zone in Settings → Checking.");
        var body = new Dictionary<string, string> { ["zone"] = settings.BrightDataUnlockerZone, ["url"] = url.ToString(), ["format"] = "raw" };
        if (settings.BrightDataCountry.Length > 0)
            body["country"] = settings.BrightDataCountry;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // It retries the store itself (new addresses, solving the check), which can take a while.
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(settings.RequestTimeoutSeconds, 90)));
        using var request = new HttpRequestMessage(HttpMethod.Post, UnlockerEndpoint) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", settings.BrightDataApiToken);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Web Unlocker didn't answer in time.");
        }
        using (response)
        {
            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            // Its own errors (bad token, unknown zone, couldn't get past the store) come with this header.
            var error = Header(response, "x-brd-error") ?? Header(response, "x-luminati-error");
            if (error != null || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired)
            {
                var what = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when error == null || error.Contains("auth", StringComparison.OrdinalIgnoreCase)
                        => "Bright Data refused the API token",
                    _ => "Web Unlocker couldn't get the page",
                };
                throw new HttpRequestException($"{what} ({(int)response.StatusCode}): {error ?? Short(html)}");
            }
            return (response.StatusCode, html);
        }
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join("; ", v) : null;

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    // ---- Bright Data Browser API (CDP) ----

    /// <summary>
    /// The rendered page from a Bright Data cloud browser. Each connection is a new session there (a new address); the
    /// page's own cookies and local storage are carried over from the last check. A session that ends under it (they cap
    /// how long one lives) is retried once with a new one.
    /// </summary>
    public async Task<string> BrightDataAsync(Site site, Uri url, List<SiteCookie> cookies, BotSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.BrightDataBrowserUrl))
            throw new HttpRequestException("The Bright Data browser isn't set up: add its address in Settings → Checking.");
        var playwright = await PlaywrightAsync(ct);
        for (var attempt = 1; ; attempt++)
        {
            IBrowser browser;
            try
            {
                browser = await playwright.Chromium.ConnectOverCDPAsync(settings.BrightDataBrowserUrl, new() { Timeout = 60000 });
            }
            catch (PlaywrightException e)
            {
                var first = e.Message.Split('\n')[0];
                throw new HttpRequestException(first.Contains("407") || first.Contains("401") || first.Contains("auth", StringComparison.OrdinalIgnoreCase)
                    ? "Bright Data refused the browser's login: check the address (its user and password) in Settings → Checking."
                    : "Couldn't reach Bright Data's browser: " + first);
            }
            try
            {
                var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
                await RestoreAsync(context, SessionFor(SiteKind.BrightData, site.Id, settings), cookies);
                var page = await context.NewPageAsync();
                // Their browser can take a while to get past a store's check: at least a minute.
                var timeoutMs = Math.Max(settings.RequestTimeoutSeconds, 60) * 1000;
                await page.GotoAsync(url.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeoutMs });
                await SolveCaptchaAsync(page);
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8000 });
                }
                catch (TimeoutException)
                {
                }
                var html = await page.ContentAsync();
                try
                {
                    Save(SiteKind.BrightData, site.Id, settings, await context.StorageStateAsync());
                }
                catch (PlaywrightException e)
                {
                    log.LogDebug("Couldn't save the Bright Data session: {Error}", e.Message);
                }
                return html;
            }
            catch (PlaywrightException e) when (attempt == 1 && SessionEnded(e) && !ct.IsCancellationRequested)
            {
                log.LogInformation("Bright Data session ended mid-check ({Error}); trying a new one", e.Message.Split('\n')[0]);
            }
            catch (PlaywrightException e)
            {
                throw new HttpRequestException("Bright Data's browser: " + e.Message.Split('\n')[0]);
            }
            catch (TimeoutException e)
            {
                throw new HttpRequestException("Bright Data's browser: " + e.Message.Split('\n')[0]);
            }
            finally
            {
                // Closing it ends the session there (they bill while it's open).
                try
                {
                    await browser.CloseAsync();
                }
                catch (PlaywrightException)
                {
                }
            }
        }
    }

    /// <summary>Bright Data solves bot checks (CAPTCHAs) by itself; this waits for that when there is one. Elsewhere it's a no-op.</summary>
    private async Task SolveCaptchaAsync(IPage page)
    {
        try
        {
            var cdp = await page.Context.NewCDPSessionAsync(page);
            var r = await cdp.SendAsync("Captcha.waitForSolve", new Dictionary<string, object> { ["detectTimeout"] = 10000 });
            if (r?.TryGetProperty("status", out var status) == true && status.GetString() is { } s && s != "not_detected")
                log.LogInformation("Bright Data CAPTCHA on {Url}: {Status}", page.Url, s);
            await cdp.DetachAsync();
        }
        catch (PlaywrightException)
        {
        }
    }

    private static bool SessionEnded(PlaywrightException e) =>
        e.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
        || e.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
        || e.Message.Contains("session", StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts the page's cookies (pasted, then saved from the last check) and local storage into a context.</summary>
    private async Task RestoreAsync(IBrowserContext context, string? state, List<SiteCookie> cookies)
    {
        var list = cookies.Select(c => new Cookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path }).ToList();
        var origins = new JsonArray();
        if (state != null && JsonNode.Parse(state) is JsonObject saved)
        {
            foreach (var c in saved["cookies"]?.AsArray() ?? [])
                if (c is JsonObject o && o["name"]?.GetValue<string>() is { } name && o["domain"]?.GetValue<string>() is { } domain)
                    list.Add(new Cookie
                    {
                        Name = name, Value = o["value"]?.GetValue<string>() ?? "", Domain = domain, Path = o["path"]?.GetValue<string>() ?? "/",
                        Expires = o["expires"]?.GetValue<float>() is > 0 and var exp ? exp : null,
                        HttpOnly = o["httpOnly"]?.GetValue<bool>(), Secure = o["secure"]?.GetValue<bool>(),
                    });
            origins = saved["origins"]?.AsArray() ?? [];
        }
        if (list.Count > 0)
            try
            {
                await context.AddCookiesAsync(list);
            }
            catch (PlaywrightException e)
            {
                log.LogWarning("Couldn't put the saved cookies back: {Error}", e.Message.Split('\n')[0]);
            }
        // Local storage: set on each page of its origin before the page's own script runs, without overwriting what the
        // page has set since.
        if (origins.Count > 0)
            await context.AddInitScriptAsync($$"""
                (() => {
                  const saved = {{origins.ToJsonString()}};
                  for (const o of saved) {
                    if (o.origin !== location.origin) continue;
                    try {
                      for (const { name, value } of o.localStorage || [])
                        if (localStorage.getItem(name) === null) localStorage.setItem(name, value);
                    } catch (e) {}
                  }
                })();
                """);
    }

    private async Task<IPlaywright> PlaywrightAsync(CancellationToken ct)
    {
        if (_playwright != null)
            return _playwright;
        await _lock.WaitAsync(ct);
        try
        {
            return _playwright ??= await Playwright.CreateAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- Camoufox ----

    private sealed record CamoufoxReply(string? Html, int Status, string? FinalUrl, JsonElement? StorageState, string? Error);

    /// <summary>The rendered page from the Camoufox service, with the page's session carried over from the last check.</summary>
    public async Task<(int Status, string Html)> CamoufoxAsync(Site site, Uri url, List<SiteCookie> cookies, BotSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.CamoufoxUrl))
            throw new HttpRequestException("Camoufox isn't set up: add the Camoufox service's address in Settings → Checking.");
        var state = SessionFor(SiteKind.Camoufox, site.Id, settings);
        var body = new Dictionary<string, object?>
        {
            ["url"] = url.ToString(),
            ["timeoutMs"] = settings.RequestTimeoutSeconds * 1000,
            ["settleMs"] = 8000,
            ["storageState"] = state == null ? null : JsonNode.Parse(state),
            ["cookies"] = cookies.Select(c => new { name = c.Name, value = c.Value, domain = c.Domain, path = c.Path }).ToList(),
        };
        if (StoreProxy.Parse(settings.ProxyUrl) is { } proxy)
            body["proxy"] = new { server = proxy.Server, username = proxy.Username, password = proxy.Password };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Navigation, the settle wait and starting Firefox the first time.
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds + 60));
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ClientName).PostAsJsonAsync(settings.CamoufoxUrl.TrimEnd('/') + "/render", body, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("The Camoufox service didn't answer in time.");
        }
        catch (HttpRequestException e)
        {
            throw new HttpRequestException($"Couldn't reach the Camoufox service at {settings.CamoufoxUrl}: {e.Message}");
        }
        using (response)
        {
            CamoufoxReply? reply;
            try
            {
                reply = await response.Content.ReadFromJsonAsync<CamoufoxReply>(new JsonSerializerOptions(JsonSerializerDefaults.Web), timeout.Token);
            }
            catch (JsonException)
            {
                throw new HttpRequestException($"The Camoufox service answered {(int)response.StatusCode} with something that isn't its JSON.");
            }
            if (reply?.Error is { } error || reply?.Html == null)
                throw new HttpRequestException("Camoufox: " + (reply?.Error ?? $"no page ({(int)response.StatusCode})"));
            if (reply.StorageState is { ValueKind: JsonValueKind.Object } s)
                Save(SiteKind.Camoufox, site.Id, settings, s.GetRawText());
            return (reply.Status, reply.Html);
        }
    }
}

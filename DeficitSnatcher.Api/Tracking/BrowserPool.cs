using DeficitSnatcher.Api.Settings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using DeficitSnatcher.Api.Data;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// The server's browser, started the first time a page needs it (a Browser-kind site's check, or a checkout), with a fresh
/// context for every use. Which browser it is: Settings → App browser (Chromium here, Bright Data's cloud browser over CDP,
/// or Camoufox over Playwright's protocol). Every context starts with the
/// page's saved session (cookies and local storage, see BrowserSessions) and saves it again when it's done.
/// Chromium It always runs with a window, like the PC helper's Chrome: headless is the
/// easiest thing for a store's bot check to spot. On Linux with no screen (Docker) it starts a virtual one (Xvfb) for it;
/// run locally, <c>dotnet run -- install-browser</c> installs it once.
/// </summary>
public sealed class BrowserPool(Pc.RunnerHub runner, Pc.WorkerPool workers, IServer server, BrowserSessions sessions, ILogger<BrowserPool> log)
    : IAsyncDisposable
{
    /// <summary>Any PC helper is connected.</summary>
    public bool PcAvailable => runner.Connected;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IBrowser> _pcBrowsers = new();
    private readonly SemaphoreSlim _pcLock = new(1, 1);
    private bool _watching;

    /// <summary>A PC helper's Chrome (see Pc/RunnerHub): its default context is the helper's profile.</summary>
    public async Task<IBrowserContext> GetPcContextAsync(int runnerId, CancellationToken ct)
    {
        if (!runner.IsConnected(runnerId))
            throw new PcHelperException($"{runner.NameOf(runnerId)} isn't connected.");
        if (!_watching)
        {
            _watching = true;
            runner.Changed += id =>
            {
                _pcBrowsers.TryRemove(id, out _);
                foreach (var k in _tabs.Keys.Where(k => k.Runner == id).ToList())
                    _tabs.TryRemove(k, out _);
            };
        }
        if (_pcBrowsers.TryGetValue(runnerId, out var b) && b.IsConnected && b.Contexts.Count > 0)
            return b.Contexts[0];
        await _pcLock.WaitAsync(ct);
        try
        {
            if (_pcBrowsers.TryGetValue(runnerId, out var b2) && b2.IsConnected && b2.Contexts.Count > 0)
                return b2.Contexts[0];
            _playwright ??= await Playwright.CreateAsync();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? "http://127.0.0.1:8080";
            var port = new Uri(address.Replace("+", "localhost").Replace("*", "localhost").Replace("[::]", "localhost")).Port;
            log.LogInformation("Connecting to {Helper}'s Chrome", runner.NameOf(runnerId));
            var browser = await _playwright.Chromium.ConnectOverCDPAsync($"ws://127.0.0.1:{port}/internal/cdp/{runner.Secret}/{runnerId}",
                new BrowserTypeConnectOverCDPOptions { Timeout = 30000 });
            _pcBrowsers[runnerId] = browser;
            return browser.Contexts.Count > 0 ? browser.Contexts[0] : await browser.NewContextAsync();
        }
        catch (PlaywrightException e)
        {
            throw new PcHelperException($"Couldn't reach Chrome on {runner.NameOf(runnerId)}: " + e.Message.Split('\n')[0]);
        }
        finally
        {
            _pcLock.Release();
        }
    }

    /// <summary>
    /// A new tab in a helper's Chrome that doesn't get in the owner's way: in the background (it never comes to the front
    /// while they're typing somewhere), or in a window of its own (Log in). Falls back to a plain new tab.
    /// </summary>
    public static async Task<IPage> OpenPcPageAsync(IBrowserContext context, bool newWindow)
    {
        if (context.Browser is not { } browser)
            return await context.NewPageAsync();
        // A bot tab opens in the background (never in front). While the owner has a sign-in window open on a helper, the site
        // gives that helper no work at all (see RunnerHub.IsSigningIn), so nothing opens behind it.
        string? targetId = null;
        try
        {
            var cdp = await browser.NewBrowserCDPSessionAsync();
            var args = new Dictionary<string, object> { ["url"] = "about:blank" };
            if (newWindow)
                args["newWindow"] = true;
            else
                args["background"] = true;
            var r = await cdp.SendAsync("Target.createTarget", args);
            targetId = r?.GetProperty("targetId").GetString();
            await cdp.DetachAsync();
        }
        catch (PlaywrightException)
        {
        }
        if (targetId == null)
            return await context.NewPageAsync();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            foreach (var p in context.Pages.Reverse())
                if (await TargetIdAsync(p) == targetId)
                    return p;
            await Task.Delay(100);
        }
        throw new PcHelperException("Chrome on the helper didn't open the tab.");
    }

    /// <summary>Which helper this context is the Chrome of (null: the server's own).</summary>
    public int? RunnerOf(IBrowserContext context)
    {
        foreach (var (id, b) in _pcBrowsers)
            if (b.Contexts.Contains(context))
                return id;
        return null;
    }

    // ---- Kept tabs: on a helper, each store page has its own tab, refreshed for every check (not opened and closed) ----

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Runner, int Site), (IPage Page, string Url)> _tabs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Runner, int Site), SemaphoreSlim> _tabGates = new();

    /// <summary>One check at a time per tab.</summary>
    public SemaphoreSlim TabGate(int runnerId, int siteId) => _tabGates.GetOrAdd((runnerId, siteId), _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The page's tab on the helper, showing it fresh: reloaded if it's open on this address already, otherwise opened (in
    /// the helper's own window) and taken there. Kept open afterwards. Call inside <see cref="TabGate"/>.
    /// </summary>
    public async Task<IPage> KeptTabAsync(int runnerId, int siteId, string url, int timeoutMs, CancellationToken ct)
    {
        var key = (runnerId, siteId);
        if (_tabs.TryGetValue(key, out var tab) && !tab.Page.IsClosed && tab.Url == url)
        {
            await tab.Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeoutMs });
            return tab.Page;
        }
        var page = tab.Page is { IsClosed: false } open ? open : await OpenPcPageAsync(await GetPcContextAsync(runnerId, ct), newWindow: false);
        _tabs[key] = (page, url);
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeoutMs });
        return page;
    }

    /// <summary>
    /// Forgets one store in this browser: its cookies (every subdomain), its site data (local storage, IndexedDB, service
    /// workers, cache storage) and the HTTP cache, so the store sees a browser it hasn't met. It's signed out of that store.
    /// </summary>
    public static async Task ClearStoreAsync(IBrowserContext context, string domain)
    {
        domain = domain.StartsWith("www.") ? domain[4..] : domain;
        await context.ClearCookiesAsync(new() { DomainRegex = new System.Text.RegularExpressions.Regex(
            $@"(^|\.){System.Text.RegularExpressions.Regex.Escape(domain)}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        var page = context.Pages.FirstOrDefault(p => !p.IsClosed) ?? await context.NewPageAsync();
        var cdp = await context.NewCDPSessionAsync(page);
        try
        {
            foreach (var origin in new[] { $"https://{domain}", $"https://www.{domain}", $"http://{domain}", $"http://www.{domain}" })
                await cdp.SendAsync("Storage.clearDataForOrigin", new Dictionary<string, object> { ["origin"] = origin, ["storageTypes"] = "all" });
            await cdp.SendAsync("Network.clearBrowserCache");
        }
        finally
        {
            await cdp.DetachAsync();
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPage, string> TargetIds = new();

    private static async Task<string?> TargetIdAsync(IPage page)
    {
        if (TargetIds.TryGetValue(page, out var known))
            return known;
        try
        {
            var s = await page.Context.NewCDPSessionAsync(page);
            var r = await s.SendAsync("Target.getTargetInfo");
            await s.DetachAsync();
            var id = r?.GetProperty("targetInfo").GetProperty("targetId").GetString();
            if (id != null)
                TargetIds.AddOrUpdate(page, id);
            return id;
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    // Hides the one property that says "a program is driving this browser".
    private const string Stealth = """
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        """;

    public const string Chromium = "chromium", BrightData = "brightdata", Camoufox = "camoufox";

    /// <summary>The server's browser: Settings → App browser.</summary>
    public static string EngineFor(BotSettings settings) => settings.AppBrowser switch
    {
        "BrightData" => BrightData,
        "Camoufox" => Camoufox,
        _ => Chromium,
    };

    public static string EngineName(string engine) => engine switch
    {
        BrightData => "Bright Data's browser",
        Camoufox => "Camoufox",
        _ => "Chromium",
    };

    /// <summary>
    /// A browser to use and dispose, on a worker (see Pc/WorkerPool): on the server, a fresh context in the app's browser
    /// (see <see cref="EngineFor"/>) with the page's saved session, through the proxy when one is set; on a PC helper, its
    /// Chrome's own profile (the owner's sign-ins), whose pages are closed when done but whose profile stays. A helper
    /// that's gone (or whose Chrome can't be reached) falls back to the server.
    /// </summary>
    /// <param name="site">The page it's for: its saved session is used and kept (null: none).</param>
    public async Task<BrowserLease> OpenAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, string worker, CancellationToken ct, Site? site = null)
    {
        if (Pc.WorkerPool.PcId(worker) is { } id && runner.IsConnected(id))
        {
            try
            {
                return new BrowserLease(await GetPcContextAsync(id, ct), owned: false, worker);
            }
            catch (PcHelperException e)
            {
                log.LogWarning("PC browser unavailable, using the server's: {Error}", e.Message);
            }
        }
        return await ServerLeaseAsync(settings, cookies.ToList(), site, ct);
    }

    /// <summary>The old form: on the PC (the best connected helper) or the server.</summary>
    public Task<BrowserLease> OpenAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, bool onPc, CancellationToken ct, Site? site = null) =>
        OpenAsync(settings, cookies, onPc ? workers.Best(null) : Pc.WorkerPool.Server, ct, site);

    private async Task<BrowserLease> ServerLeaseAsync(BotSettings settings, List<SiteCookie> cookies, Site? site, CancellationToken ct)
    {
        var engine = EngineFor(settings);
        var siteId = site?.Id ?? 0;
        var state = await sessions.LoadAsync(siteId, engine, settings, ct);
        IBrowserContext context;
        IBrowser? ownBrowser = null;
        switch (engine)
        {
            case Camoufox:
                context = await CamoufoxContextAsync(settings, state, ct);
                break;
            case BrightData:
                (ownBrowser, context) = await BrightDataContextAsync(settings, state, ct);
                break;
            default:
                context = await NewContextAsync(settings, state, ct);
                break;
        }
        try
        {
            await AddPastedCookiesAsync(context, cookies, state);
        }
        catch (PlaywrightException e)
        {
            log.LogWarning("Couldn't add the page's cookies: {Error}", e.Message.Split('\n')[0]);
        }
        Func<IBrowserContext, Task>? save = siteId > 0 && settings.BrowserSessionMinutes > 0
            ? async c => await sessions.SaveAsync(siteId, engine, await c.StorageStateAsync())
            : null;
        return new BrowserLease(context, owned: true, Pc.WorkerPool.Server, engine, save, ownBrowser, log);
    }

    /// <summary>The cookies pasted on the page. With a saved session, only the ones it doesn't have: the store may have
    /// renewed them since, and the session's are the fresher.</summary>
    private static async Task AddPastedCookiesAsync(IBrowserContext context, List<SiteCookie> cookies, string? state)
    {
        var have = new HashSet<(string, string)>();
        if (state != null && System.Text.Json.Nodes.JsonNode.Parse(state)?["cookies"] is System.Text.Json.Nodes.JsonArray saved)
            foreach (var c in saved)
                if (c?["name"]?.GetValue<string>() is { } n && c["domain"]?.GetValue<string>() is { } d)
                    have.Add((n, d.TrimStart('.')));
        var list = cookies.Where(c => !have.Contains((c.Name, c.Domain.TrimStart('.'))))
            .Select(c => new Cookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path }).ToList();
        if (list.Count > 0)
            await context.AddCookiesAsync(list);
    }

    // ---- Camoufox: a Firefox that hides automation, run as a Playwright server (DeficitSnatcher.Camoufox) ----

    private IBrowser? _camoufox;
    private string? _camoufoxUrl;

    private async Task<IBrowserContext> CamoufoxContextAsync(BotSettings settings, string? state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.CamoufoxUrl))
            throw new BrowserEngineException("Camoufox isn't set up: add its address in Settings → Checking (ws://camoufox:4444/camoufox in Docker).");
        var browser = await CamoufoxBrowserAsync(settings.CamoufoxUrl, ct);
        // Its own fingerprint (user agent, screen, fonts, timezone…) stays as Camoufox made it: nothing is overridden here.
        var options = new BrowserNewContextOptions { StorageState = state };
        if (StoreProxy.Parse(settings.ProxyUrl) is { } proxy)
            options.Proxy = new Proxy { Server = proxy.Server, Username = proxy.Username, Password = proxy.Password };
        try
        {
            return await browser.NewContextAsync(options);
        }
        catch (PlaywrightException) when (!browser.IsConnected)
        {
            // It restarted under us: once more on a new connection.
            return await (await CamoufoxBrowserAsync(settings.CamoufoxUrl, ct)).NewContextAsync(options);
        }
    }

    private async Task<IBrowser> CamoufoxBrowserAsync(string url, CancellationToken ct)
    {
        if (_camoufox is { IsConnected: true } && _camoufoxUrl == url)
            return _camoufox;
        await _lock.WaitAsync(ct);
        try
        {
            if (_camoufox is { IsConnected: true } && _camoufoxUrl == url)
                return _camoufox;
            _playwright ??= await Playwright.CreateAsync();
            log.LogInformation("Connecting to Camoufox at {Url}", url);
            try
            {
                _camoufox = await _playwright.Firefox.ConnectAsync(url, new BrowserTypeConnectOptions { Timeout = 30000 });
            }
            catch (PlaywrightException e)
            {
                var first = e.Message.Split('\n')[0];
                throw new BrowserEngineException($"Couldn't reach Camoufox at {url}: " + (first.Contains("428")
                    ? "its Playwright isn't the API's version (both must be the same: see DeficitSnatcher.Camoufox/requirements.txt)."
                    : first));
            }
            _camoufoxUrl = url;
            return _camoufox;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- Bright Data Browser API: a Chrome in their cloud, over CDP. Each connection is a new session there. ----

    private async Task<(IBrowser, IBrowserContext)> BrightDataContextAsync(BotSettings settings, string? state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.BrightDataBrowserUrl))
            throw new BrowserEngineException("Bright Data's browser isn't set up: add its address in Settings → Checking.");
        _playwright ??= await Playwright.CreateAsync();
        IBrowser browser;
        try
        {
            browser = await _playwright.Chromium.ConnectOverCDPAsync(settings.BrightDataBrowserUrl, new() { Timeout = 60000 });
        }
        catch (PlaywrightException e)
        {
            var first = e.Message.Split('\n')[0];
            throw new BrowserEngineException(first.Contains("407") || first.Contains("401") || first.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? "Bright Data refused the browser's login: check its address (user and password) in Settings → Checking."
                : "Couldn't reach Bright Data's browser: " + first);
        }
        try
        {
            // Their browser takes no context options (it sets its own fingerprint): the session goes in its own context.
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
            if (state != null)
                await RestoreStateAsync(context, state);
            return (browser, context);
        }
        catch
        {
            await browser.CloseAsync();
            throw;
        }
    }

    /// <summary>A saved storage state put into a context that already exists: cookies, and local storage set on each page of
    /// its origin before the page's own script runs (without overwriting what the page has set since).</summary>
    private async Task RestoreStateAsync(IBrowserContext context, string state)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(state) is not System.Text.Json.Nodes.JsonObject saved)
            return;
        var cookies = new List<Cookie>();
        foreach (var c in saved["cookies"]?.AsArray() ?? [])
            if (c is System.Text.Json.Nodes.JsonObject o && o["name"]?.GetValue<string>() is { } name && o["domain"]?.GetValue<string>() is { } domain)
                cookies.Add(new Cookie
                {
                    Name = name, Value = o["value"]?.GetValue<string>() ?? "", Domain = domain, Path = o["path"]?.GetValue<string>() ?? "/",
                    Expires = o["expires"]?.GetValue<float>() is > 0 and var exp ? exp : null,
                    HttpOnly = o["httpOnly"]?.GetValue<bool>(), Secure = o["secure"]?.GetValue<bool>(),
                });
        if (cookies.Count > 0)
            try
            {
                await context.AddCookiesAsync(cookies);
            }
            catch (PlaywrightException e)
            {
                log.LogWarning("Couldn't put the saved cookies back: {Error}", e.Message.Split('\n')[0]);
            }
        if (saved["origins"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } origins)
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

    /// <summary>Bright Data's browser solves bot checks (CAPTCHAs) itself: this waits for it when there is one. A no-op in
    /// other browsers.</summary>
    public async Task WaitForCaptchaAsync(BrowserLease lease, IPage page)
    {
        if (lease.Engine != BrightData)
            return;
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

    // ---- Chromium, here ----

    private async Task<IBrowserContext> NewContextAsync(BotSettings settings, string? state, CancellationToken ct)
    {
        var browser = await GetBrowserAsync(ct);
        var options = new BrowserNewContextOptions
        {
            Locale = "en-CA",
            TimezoneId = "America/Toronto",
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Accept-Language"] = "en-CA,en;q=0.9" },
            StorageState = state,
        };
        if (StoreProxy.Parse(settings.ProxyUrl) is { } proxy)
            options.Proxy = new Proxy { Server = proxy.Server, Username = proxy.Username, Password = proxy.Password };
        var context = await browser.NewContextAsync(options);
        await context.AddInitScriptAsync(Stealth);
        return context;
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: true })
            return _browser;
        await _lock.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true })
                return _browser;
            _playwright ??= await Playwright.CreateAsync();
            var display = await DisplayAsync(ct);
            log.LogInformation("Starting Chromium (with a window, on {Display})", display ?? "the desktop");
            // The full browser with a window, set up like the PC helper's Chrome: no headless mode, no automation flag, and
            // its own user agent (a headed Chrome's, so nothing to fake).
            var options = new BrowserTypeLaunchOptions
            {
                Headless = false,
                Channel = "chromium",
                Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--disable-dev-shm-usage", "--window-size=1366,900"],
            };
            if (display != null)
                options.Env = new Dictionary<string, string> { ["DISPLAY"] = display };
            _browser = await _playwright.Chromium.LaunchAsync(options);
            return _browser;
        }
        finally
        {
            _lock.Release();
        }
    }

    private System.Diagnostics.Process? _xvfb;
    private string? _xvfbDisplay;

    /// <summary>
    /// The screen Chromium's window goes on: the desktop's (DISPLAY, or not Linux), else a virtual one (Xvfb) started
    /// here and kept while the app runs (started again if it died). Xvfb picks a free display number itself.
    /// </summary>
    private async Task<string?> DisplayAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return null;
        if (_xvfb is { HasExited: false } && _xvfbDisplay != null)
            return _xvfbDisplay;
        var start = new System.Diagnostics.ProcessStartInfo("Xvfb")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-displayfd", "1", "-screen", "0", "1920x1080x24", "-nolisten", "tcp" })
            start.ArgumentList.Add(a);
        System.Diagnostics.Process xvfb;
        try
        {
            xvfb = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Xvfb didn't start.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("No screen for Chromium's window: install Xvfb (apt-get install xvfb) or run with a desktop.");
        }
        // Its warnings (keyboard maps) are noise: read and dropped so it never blocks on a full pipe.
        _ = xvfb.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        string? number;
        try
        {
            number = (await xvfb.StandardOutput.ReadLineAsync(timeout.Token))?.Trim();
        }
        catch (OperationCanceledException)
        {
            number = null;
        }
        if (string.IsNullOrEmpty(number) || !int.TryParse(number, out _))
        {
            try
            {
                xvfb.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            throw new InvalidOperationException("The virtual screen (Xvfb) for Chromium's window didn't start.");
        }
        _xvfb = xvfb;
        _xvfbDisplay = ":" + number;
        log.LogInformation("Started a virtual screen (Xvfb) on {Display}", _xvfbDisplay);
        return _xvfbDisplay;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
            await _browser.CloseAsync();
        if (_camoufox is { IsConnected: true })
            await _camoufox.CloseAsync();
        _playwright?.Dispose();
        try
        {
            _xvfb?.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>A browser borrowed for one job: pages opened through it are closed when it's disposed, and a context made for the
/// job (on the server) is closed too, after its session is saved (<paramref name="save"/>); a Bright Data browser is closed
/// (which ends the session there: they bill while it's open). The PC's profile is never closed.</summary>
public sealed class BrowserLease(IBrowserContext context, bool owned, string worker, string engine = BrowserPool.Chromium,
    Func<IBrowserContext, Task>? save = null, IBrowser? ownBrowser = null, ILogger? log = null) : IAsyncDisposable
{
    private readonly List<IPage> _pages = [];

    public IBrowserContext Context => context;
    /// <summary>Which browser: chromium, brightdata or camoufox (see BrowserPool); the PC helper's Chrome is chromium.</summary>
    public string Engine => engine;
    public bool OnPc => !owned;
    /// <summary>Where it really runs: "server" or "pc:{id}".</summary>
    public string Worker => worker;

    public async Task<IPage> NewPageAsync()
    {
        // On a helper: a background tab, so it never jumps in front of what the owner is doing there.
        var page = owned ? await context.NewPageAsync() : await BrowserPool.OpenPcPageAsync(context, newWindow: false);
        lock (_pages)
            _pages.Add(page);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (owned)
        {
            if (save != null)
                try
                {
                    await save(context);
                }
                catch (Exception e)
                {
                    log?.LogWarning("Browser session not saved: {Error}", e.Message.Split('\n')[0]);
                }
            try
            {
                if (ownBrowser != null)
                    await ownBrowser.CloseAsync();
                else
                    await context.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // Already gone (the session ended, or the browser restarted).
            }
            return;
        }
        List<IPage> pages;
        lock (_pages)
            pages = [.. _pages];
        foreach (var p in pages)
            try
            {
                await p.CloseAsync();
            }
            catch (PlaywrightException)
            {
            }
    }
}

/// <summary>The page's browser on the server can't be had: Bright Data or Camoufox not set up, refused, or unreachable.</summary>
public sealed class BrowserEngineException(string message) : Exception(message);

/// <summary>The PC helper isn't there, or its Chrome can't be reached.</summary>
public sealed class PcHelperException(string message) : Exception(message);

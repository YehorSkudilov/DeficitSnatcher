using DeficitSnatcher.Api.Settings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// One Chromium, started the first time a page needs it (a Browser-kind site's check, or a checkout), and a fresh
/// context (its own cookies) for every use. It always runs with a window, like the PC helper's Chrome: headless is the
/// easiest thing for a store's bot check to spot. In Docker the window is on a virtual screen (Xvfb, see Dockerfile);
/// run locally, <c>dotnet run -- install-browser</c> installs it once.
/// </summary>
public sealed class BrowserPool(Pc.RunnerHub runner, Pc.WorkerPool workers, IServer server, ILogger<BrowserPool> log) : IAsyncDisposable
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

    /// <summary>
    /// A browser to use and dispose, on a worker (see Pc/WorkerPool): on the server, a fresh context (its own cookies,
    /// through the proxy when one is set); on a PC helper, its Chrome's own profile (the owner's sign-ins), whose pages are
    /// closed when done but whose profile stays. A helper that's gone (or whose Chrome can't be reached) falls back to the
    /// server.
    /// </summary>
    public async Task<BrowserLease> OpenAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, string worker, CancellationToken ct)
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
        return new BrowserLease(await NewContextAsync(settings, cookies, ct), owned: true, Pc.WorkerPool.Server);
    }

    /// <summary>The old form: on the PC (the best connected helper) or the server.</summary>
    public Task<BrowserLease> OpenAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, bool onPc, CancellationToken ct) =>
        OpenAsync(settings, cookies, onPc ? workers.Best(null) : Pc.WorkerPool.Server, ct);

    private async Task<IBrowserContext> NewContextAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, CancellationToken ct)
    {
        var browser = await GetBrowserAsync(ct);
        var options = new BrowserNewContextOptions
        {
            Locale = "en-CA",
            TimezoneId = "America/Toronto",
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Accept-Language"] = "en-CA,en;q=0.9" },
        };
        if (StoreProxy.Parse(settings.ProxyUrl) is { } proxy)
            options.Proxy = new Proxy { Server = proxy.Server, Username = proxy.Username, Password = proxy.Password };
        var context = await browser.NewContextAsync(options);
        await context.AddInitScriptAsync(Stealth);
        var list = cookies.Select(c => new Cookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path }).ToList();
        if (list.Count > 0)
            await context.AddCookiesAsync(list);
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
            log.LogInformation("Starting Chromium (with a window, on {Display})", Environment.GetEnvironmentVariable("DISPLAY") ?? "the desktop");
            // The full browser with a window, set up like the PC helper's Chrome: no headless mode, no automation flag, and
            // its own user agent (a headed Chrome's, so nothing to fake).
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Channel = "chromium",
                Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--disable-dev-shm-usage", "--window-size=1366,900"],
            });
            return _browser;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
            await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}

/// <summary>A browser borrowed for one job: pages opened through it are closed when it's disposed, and a context made for the
/// job (on the server) is closed too. The PC's profile is never closed.</summary>
public sealed class BrowserLease(IBrowserContext context, bool owned, string worker) : IAsyncDisposable
{
    private readonly List<IPage> _pages = [];

    public IBrowserContext Context => context;
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
            await context.CloseAsync();
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

/// <summary>The PC helper isn't there, or its Chrome can't be reached.</summary>
public sealed class PcHelperException(string message) : Exception(message);

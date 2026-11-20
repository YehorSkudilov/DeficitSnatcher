using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Data;
using Microsoft.Playwright;
using System.Collections.Concurrent;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// The server's browser, started the first time a page needs it (a Browser-kind site's check, or a checkout), with a fresh
/// context for every use. Which browser it is: Settings → App browser (Chromium here, or Camoufox over Playwright's
/// protocol). Every context starts with the
/// page's saved session (cookies and local storage, see BrowserSessions) and saves it again when it's done.
/// Chromium always runs with a window: headless is the easiest thing for a store's bot check to spot. On Linux with no screen (Docker) it starts a virtual one (Xvfb) for it;
/// run locally, <c>dotnet run -- install-browser</c> installs it once.
/// </summary>
public sealed class BrowserPool(BrowserSessions sessions, ILogger<BrowserPool> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    // StreetMerchant defaults to one lookup thread per store. Serialize browser contexts for a site as well:
    // overlapping checks/checkouts can otherwise load and save the same storage state out of order.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _siteLocks = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    // Hides the one property that says "a program is driving this browser".
    private const string Stealth = """
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        """;

    public const string Chromium = "chromium", Camoufox = "camoufox";

    /// <summary>The server's browser: Settings → App browser.</summary>
    public static string EngineFor(BotSettings settings) => settings.AppBrowser == "Chromium" ? Chromium : Camoufox;

    public static string EngineName(string engine) => engine == Camoufox ? "Camoufox" : "Chromium";

    /// <summary>A browser to use and dispose: a fresh context in the app's browser (see <see cref="EngineFor"/>) with the
    /// page's saved session.</summary>
    /// <param name="site">The page it's for: its saved session is used and kept (null: none).</param>
    public async Task<BrowserLease> OpenAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, CancellationToken ct, Site? site = null)
    {
        SemaphoreSlim? siteLock = null;
        if (site is { Id: > 0 })
        {
            siteLock = _siteLocks.GetOrAdd(site.Id, _ => new SemaphoreSlim(1, 1));
            await siteLock.WaitAsync(ct);
        }
        try
        {
            return await ServerLeaseAsync(settings, cookies.ToList(), site, ct, siteLock);
        }
        catch
        {
            siteLock?.Release();
            throw;
        }
    }

    private async Task<BrowserLease> ServerLeaseAsync(BotSettings settings, List<SiteCookie> cookies, Site? site, CancellationToken ct,
        SemaphoreSlim? siteLock)
    {
        var engine = EngineFor(settings);
        var siteId = site?.Id ?? 0;
        if (engine == Camoufox)
            try
            {
                await CamoufoxBrowserAsync(settings.CamoufoxUrl, ct);
            }
            catch (BrowserEngineException e)
            {
                // Down (restarting, or not deployed): this job runs in Chromium rather than failing, without the page's
                // session (that stays for Camoufox's return).
                if (DateTimeOffset.UtcNow - _camoufoxWarnedAt > TimeSpan.FromMinutes(10))
                {
                    _camoufoxWarnedAt = DateTimeOffset.UtcNow;
                    log.LogWarning("Camoufox unavailable, using Chromium meanwhile: {Error}", e.Message);
                }
                engine = Chromium;
                siteId = 0;
            }
        var state = await sessions.LoadAsync(siteId, engine, settings, ct);
        var context = engine == Camoufox ? await CamoufoxContextAsync(settings, state, ct) : await NewContextAsync(settings, state, ct);
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
        return new BrowserLease(context, engine, save, log, siteLock);
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
    private DateTimeOffset _camoufoxWarnedAt;

    private async Task<IBrowserContext> CamoufoxContextAsync(BotSettings settings, string? state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.CamoufoxUrl))
            throw new BrowserEngineException($"Camoufox isn't set up: add its address in Settings → Checking ({BotSettings.DefaultCamoufoxUrl} in Docker).");
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

    private int _camoufoxHangs;

    /// <summary>A check on Camoufox got its page: the connection is fine.</summary>
    public void CamoufoxAnswered() => _camoufoxHangs = 0;

    /// <summary>
    /// A check hung on Camoufox. Two in a row with nothing working between: the connection is dropped and the next check
    /// connects afresh. The old one is closed a few minutes later, not now: closing it would kill every other check
    /// still on it.
    /// </summary>
    public void CamoufoxHung()
    {
        if (Interlocked.Increment(ref _camoufoxHangs) < 2)
            return;
        _camoufoxHangs = 0;
        var old = _camoufox;
        _camoufox = null;
        if (old == null)
            return;
        log.LogWarning("Camoufox stopped answering; reconnecting");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(3));
            try
            {
                await old.CloseAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception)
            {
                // Gone or wedged either way.
            }
        });
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
                    : first + " Is the camoufox container running? Or pick Chromium in Settings → Checking → App browser."));
            }
            _camoufoxUrl = url;
            return _camoufox;
        }
        finally
        {
            _lock.Release();
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
            // The full browser with a window: no headless mode, no automation flag, and
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

/// <summary>A browser borrowed for one job: its context is closed when it's disposed, after its session is saved
/// (<paramref name="save"/>).</summary>
public sealed class BrowserLease(IBrowserContext context, string engine = BrowserPool.Chromium, Func<IBrowserContext, Task>? save = null,
    ILogger? log = null, SemaphoreSlim? siteLock = null) : IAsyncDisposable
{
    public IBrowserContext Context => context;
    /// <summary>Which browser: chromium or camoufox (see BrowserPool).</summary>
    public string Engine => engine;

    public Task<IPage> NewPageAsync() => context.NewPageAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (save != null)
            {
                try
                {
                    await save(context).WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception e)
                {
                    log?.LogWarning("Browser session not saved: {Error}", e.Message.Split('\n')[0]);
                }
            }
            try
            {
                await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (PlaywrightException)
            {
                // Already gone (the browser restarted).
            }
            catch (TimeoutException)
            {
                log?.LogWarning("Browser tab didn't close in 10 s; left behind");
            }
        }
        finally
        {
            siteLock?.Release();
        }
    }
}

/// <summary>The page's browser on the server can't be had: Camoufox not set up, or unreachable.</summary>
public sealed class BrowserEngineException(string message) : Exception(message);

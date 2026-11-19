using DeficitSnatcher.Api.Settings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// One headless Chromium, started the first time a page needs it (a Browser-kind site's check, or a checkout), and
/// a fresh context (its own cookies) for every use. The Docker image installs it (see Dockerfile); run locally,
/// <c>dotnet run -- install-browser</c> installs it once.
/// </summary>
public sealed class BrowserPool(Pc.RunnerHub runner, IServer server, ILogger<BrowserPool> log) : IAsyncDisposable
{
    /// <summary>Pages set to run on the PC actually do so now (its helper is connected).</summary>
    public bool PcAvailable => runner.Connected;

    private IBrowser? _pcBrowser;
    private readonly SemaphoreSlim _pcLock = new(1, 1);
    private bool _watching;

    /// <summary>The PC's Chrome, through the helper (see Pc/RunnerHub): its default context is the helper's profile.</summary>
    private async Task<IBrowserContext> GetPcContextAsync(CancellationToken ct)
    {
        if (!runner.Connected)
            throw new PcHelperException("Your PC helper isn't connected: start it on your computer (Settings → PC helper).");
        if (!_watching)
        {
            _watching = true;
            runner.Changed += () => _pcBrowser = null;
        }
        if (_pcBrowser is { IsConnected: true } b && b.Contexts.Count > 0)
            return b.Contexts[0];
        await _pcLock.WaitAsync(ct);
        try
        {
            if (_pcBrowser is { IsConnected: true } b2 && b2.Contexts.Count > 0)
                return b2.Contexts[0];
            _playwright ??= await Playwright.CreateAsync();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? "http://127.0.0.1:8080";
            var port = new Uri(address.Replace("+", "localhost").Replace("*", "localhost").Replace("[::]", "localhost")).Port;
            log.LogInformation("Connecting to the PC's Chrome");
            _pcBrowser = await _playwright.Chromium.ConnectOverCDPAsync($"ws://127.0.0.1:{port}/internal/cdp/{runner.Secret}",
                new BrowserTypeConnectOverCDPOptions { Timeout = 30000 });
            return _pcBrowser.Contexts.Count > 0 ? _pcBrowser.Contexts[0] : await _pcBrowser.NewContextAsync();
        }
        catch (PlaywrightException e)
        {
            throw new PcHelperException("Couldn't reach Chrome on your PC: " + e.Message.Split('\n')[0]);
        }
        finally
        {
            _pcLock.Release();
        }
    }

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private string _userAgent = "";

    // Hides the one property that says "a program is driving this browser".
    private const string Stealth = """
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        """;

    /// <summary>
    /// A browser to use and dispose: on the server, a fresh context (its own cookies, through the proxy when one is set);
    /// on the owner's PC (<paramref name="onPc"/>), their Chrome's own profile (their sign-ins), whose pages are closed when
    /// done but whose profile stays.
    /// </summary>
    public async Task<BrowserLease> OpenAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, bool onPc, CancellationToken ct)
    {
        // On the PC when its helper is connected; otherwise on the server, as for any page (the store sometimes answers it).
        if (onPc && runner.Connected)
        {
            try
            {
                return new BrowserLease(await GetPcContextAsync(ct), owned: false);
            }
            catch (PcHelperException e)
            {
                log.LogWarning("PC browser unavailable, using the server's: {Error}", e.Message);
            }
        }
        return new BrowserLease(await NewContextAsync(settings, cookies, ct), owned: true);
    }

    /// <summary>A fresh browser context (its own cookies), through the proxy when one is set in Settings.</summary>
    private async Task<IBrowserContext> NewContextAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, CancellationToken ct)
    {
        var browser = await GetBrowserAsync(ct);
        var options = new BrowserNewContextOptions
        {
            // The real Chrome version on Linux, as a desktop Chrome would say (no "HeadlessChrome"), so it matches what the
            // browser itself reports.
            UserAgent = _userAgent,
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
            log.LogInformation("Starting Chromium (new headless mode)");
            // Channel "chromium": the full browser in its new headless mode, which looks like ordinary Chrome to bot
            // checks; the default "headless shell" is a stripped-down build that's easy to spot.
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Channel = "chromium",
                Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--disable-dev-shm-usage"],
            });
            var major = _browser.Version.Split('.')[0];
            _userAgent = $"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
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
public sealed class BrowserLease(IBrowserContext context, bool owned) : IAsyncDisposable
{
    private readonly List<IPage> _pages = [];

    public IBrowserContext Context => context;
    public bool OnPc => !owned;

    public async Task<IPage> NewPageAsync()
    {
        var page = await context.NewPageAsync();
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

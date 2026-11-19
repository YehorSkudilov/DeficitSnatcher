using DeficitSnatcher.Api.Settings;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// One headless Chromium, started the first time a page needs it (a Browser-kind site's check, or a checkout), and
/// a fresh context (its own cookies) for every use. The Docker image installs it (see Dockerfile); run locally,
/// <c>dotnet run -- install-browser</c> installs it once.
/// </summary>
public sealed class BrowserPool(ILogger<BrowserPool> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private string _userAgent = "";

    // Hides the one property that says "a program is driving this browser".
    private const string Stealth = """
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        """;

    /// <summary>A fresh browser context (its own cookies), through the proxy when one is set in Settings.</summary>
    public async Task<IBrowserContext> NewContextAsync(BotSettings settings, IEnumerable<SiteCookie> cookies, CancellationToken ct)
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

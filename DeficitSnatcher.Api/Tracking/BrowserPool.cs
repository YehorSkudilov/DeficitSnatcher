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

    public async Task<IBrowserContext> NewContextAsync(string userAgent, IEnumerable<SiteCookie> cookies, CancellationToken ct)
    {
        var browser = await GetBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = userAgent,
            Locale = "en-CA",
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        });
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
            log.LogInformation("Starting headless Chromium");
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--disable-dev-shm-usage"],
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

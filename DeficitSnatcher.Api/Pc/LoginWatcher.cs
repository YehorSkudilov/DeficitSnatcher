using System.Collections.Concurrent;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Pc;

public sealed record StoreLoginDto(string Domain, string Status, DateTimeOffset? CheckedAt, string? Detail, string AccountUrl);

/// <summary>
/// Which stores each PC helper's Chrome is signed in to: it opens the store's account page in that Chrome now and then (when
/// the helper connects, then every few hours) and looks: a sign-in form means signed out, a bot check means the owner has to
/// prove they're human. Either sends an alert, once, so the owner can sign in from the Helpers tab (which opens the store's
/// page in that Chrome, in front). It never types a password.
/// </summary>
public sealed class LoginWatcher(IServiceScopeFactory scopes, RunnerHub hub, BrowserPool browsers, Notifier notifier,
    ILogger<LoginWatcher> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<int, DateTimeOffset> _checked = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Each store's account page: signed out, it sends you to sign in.</summary>
    private static readonly Dictionary<string, string> AccountPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amazon.ca"] = "https://www.amazon.ca/gp/css/homepage.html",
        ["amazon.com"] = "https://www.amazon.com/gp/css/homepage.html",
        ["walmart.ca"] = "https://www.walmart.ca/en/account",
        ["walmart.com"] = "https://www.walmart.com/account",
        ["costco.ca"] = "https://www.costco.ca/AccountInformationView",
        ["costco.com"] = "https://www.costco.com/AccountInformationView",
        ["bestbuy.ca"] = "https://www.bestbuy.ca/account/en-ca",
        ["bestbuy.com"] = "https://www.bestbuy.com/profile/c/account",
        ["ebgames.ca"] = "https://www.ebgames.ca/Profile",
        ["gamestop.ca"] = "https://www.gamestop.ca/Profile",
        ["thesource.ca"] = "https://www.thesource.ca/en-ca/my-account",
        ["canadacomputers.com"] = "https://www.canadacomputers.com/en/my-account",
        ["memoryexpress.com"] = "https://www.memoryexpress.com/User/Account",
        ["thebrick.com"] = "https://www.thebrick.com/account",
        ["staples.ca"] = "https://www.staples.ca/account",
        ["londondrugs.com"] = "https://www.londondrugs.com/account",
        ["direct.playstation.com"] = "https://direct.playstation.com/en-ca/my-account",
    };

    private static readonly string[] SignInUrlSigns =
        ["/ap/signin", "signin", "sign-in", "sign_in", "/login", "log-in", "logon", "/auth", "account/login", "identity", "LogonForm"];

    public static string Domain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return "";
        var host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    public static string AccountUrl(string domain, string? anyPageUrl = null)
    {
        if (AccountPages.TryGetValue(domain, out var known))
            return known;
        var origin = anyPageUrl != null && Uri.TryCreate(anyPageUrl, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : $"https://www.{domain}";
        return origin + "/account";
    }

    /// <summary>The stores of the pages being watched: domain → one page there (for its address).</summary>
    private async Task<Dictionary<string, (int SiteId, string Url, string Name)>> StoresAsync()
    {
        using var scope = scopes.CreateScope();
        var sites = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sites.AsNoTracking()
            .Where(s => s.Enabled).OrderBy(s => s.Id).Select(s => new { s.Id, s.Url, s.Name }).ToListAsync();
        var stores = new Dictionary<string, (int, string, string)>();
        foreach (var s in sites)
        {
            var d = Domain(s.Url);
            if (d.Length > 0)
                stores.TryAdd(d, (s.Id, s.Url, s.Name));
        }
        return stores;
    }

    /// <summary>What's known for a helper, for every store being watched (unknown ones too).</summary>
    public async Task<List<StoreLoginDto>> ListAsync(int runnerId)
    {
        var stores = await StoresAsync();
        using var scope = scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RunnerLogins.AsNoTracking()
            .Where(l => l.RunnerId == runnerId).ToDictionaryAsync(l => l.Domain);
        return stores.OrderBy(s => s.Key).Select(s => rows.TryGetValue(s.Key, out var r)
                ? new StoreLoginDto(s.Key, r.Status, r.CheckedAt, r.Detail, AccountUrl(s.Key, s.Value.Url))
                : new StoreLoginDto(s.Key, "unknown", null, null, AccountUrl(s.Key, s.Value.Url)))
            .ToList();
    }

    /// <summary>Check this helper again soon (the next minute).</summary>
    public void CheckSoon(int runnerId) => _checked.TryRemove(runnerId, out _);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        hub.Changed += id =>
        {
            if (hub.IsConnected(id))
                CheckSoon(id);
        };
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stop);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stop.IsCancellationRequested)
        {
            foreach (var id in hub.ConnectedIds)
            {
                if (_checked.TryGetValue(id, out var at) && DateTimeOffset.UtcNow - at < Every)
                    continue;
                _checked[id] = DateTimeOffset.UtcNow;
                try
                {
                    await CheckAllAsync(id, stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    log.LogWarning("Couldn't check helper {Id}'s sign-ins: {Error}", id, e.Message);
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Opens each store's account page in the helper's Chrome, one by one, and notes what it shows.</summary>
    public async Task CheckAllAsync(int runnerId, CancellationToken ct, string? onlyDomain = null)
    {
        await _one.WaitAsync(ct);
        try
        {
            foreach (var (domain, site) in await StoresAsync())
            {
                if (onlyDomain != null && domain != onlyDomain)
                    continue;
                if (!hub.IsConnected(runnerId))
                    return;
                var (status, detail) = await CheckOneAsync(runnerId, AccountUrl(domain, site.Url), ct);
                await SaveAsync(runnerId, domain, status, detail, site.SiteId, site.Url);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<(string Status, string? Detail)> CheckOneAsync(int runnerId, string url, CancellationToken ct)
    {
        IPage? page = null;
        try
        {
            var context = await browsers.GetPcContextAsync(runnerId, ct);
            page = await context.NewPageAsync();
            try
            {
                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            }
            catch (TimeoutException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(4), ct); // redirects to sign in, scripts drawing the page
            return await ReadAsync(page);
        }
        catch (PcHelperException e)
        {
            return ("error", e.Message);
        }
        catch (PlaywrightException e)
        {
            return ("error", e.Message.Split('\n')[0]);
        }
        finally
        {
            if (page != null)
                try
                {
                    await page.CloseAsync();
                }
                catch (PlaywrightException)
                {
                }
        }
    }

    /// <summary>What an open store page says about signing in.</summary>
    private static async Task<(string Status, string? Detail)> ReadAsync(IPage page)
    {
        var html = await page.ContentAsync();
        if (BlockDetector.Detect(html) is { } vendor)
            return ("humanCheck", $"The store wants you to prove you're human ({vendor}).");
        var finalUrl = page.Url;
        var path = Uri.TryCreate(finalUrl, UriKind.Absolute, out var u) ? u.PathAndQuery : finalUrl;
        var passwordShown = false;
        try
        {
            passwordShown = await page.Locator("input[type=password]").First.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
        }
        if (passwordShown || SignInUrlSigns.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return ("signedOut", "The store asked to sign in.");
        string title = "";
        try
        {
            title = (await page.TitleAsync()).Trim();
        }
        catch (PlaywrightException)
        {
        }
        return ("signedIn", title.Length > 0 ? title[..Math.Min(title.Length, 200)] : null);
    }

    private async Task SaveAsync(int runnerId, string domain, string status, string? detail, int siteId, string? pageUrl)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.RunnerKeys.AnyAsync(r => r.Id == runnerId))
            return;
        var row = await db.RunnerLogins.FirstOrDefaultAsync(l => l.RunnerId == runnerId && l.Domain == domain);
        var before = row?.Status;
        if (row == null)
            db.RunnerLogins.Add(row = new RunnerLogin { RunnerId = runnerId, Domain = domain });
        // A helper that couldn't be reached says nothing new about signing in.
        if (status == "error" && before is "signedIn" or "signedOut" or "humanCheck")
        {
            row.Detail = detail;
            row.CheckedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return;
        }
        row.Status = status;
        row.Detail = detail?[..Math.Min(detail.Length, 500)];
        row.CheckedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (status == before)
            return;
        var name = hub.NameOf(runnerId);
        var link = notifier.Link("helpers");
        if (status == "signedOut")
            await notifier.NotifyAsync(NotifyKind.ActionNeeded, EventLevel.Warning,
                $"{name} isn't signed in to {domain}. Sign in so it can check out: on the site's Helpers tab, press Log in next to {domain} "
                + $"and the store opens in that computer's Chrome.", siteId, AccountUrl(domain, pageUrl), link);
        else if (status == "humanCheck")
            await notifier.NotifyAsync(NotifyKind.ActionNeeded, EventLevel.Warning,
                $"{domain} wants you to prove you're human in {name}'s Chrome. On the Helpers tab press Log in next to {domain} "
                + "to bring it up and do it there.", siteId, AccountUrl(domain, pageUrl), link);
        else if (status == "signedIn" && before is "signedOut" or "humanCheck")
            await notifier.LogAsync(EventLevel.Good, $"{name} is signed in to {domain}.", siteId);
    }

    /// <summary>
    /// Opens the store's account page (it sends you to sign in if you aren't) in the helper's Chrome, un-minimised and in
    /// front, and leaves it open for the owner. Then it watches that tab for ten minutes and notes when they're signed in.
    /// </summary>
    public async Task OpenAsync(int runnerId, string domain, CancellationToken ct)
    {
        var stores = await StoresAsync();
        var url = AccountUrl(domain, stores.TryGetValue(domain, out var s) ? s.Url : null);
        var context = await browsers.GetPcContextAsync(runnerId, ct);
        var page = await context.NewPageAsync();
        await HumanCheck.ShowAsync(page);
        try
        {
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
        }
        catch (TimeoutException)
        {
        }
        await HumanCheck.ShowAsync(page);
        var siteId = s.SiteId;
        _ = Task.Run(async () =>
        {
            var until = DateTimeOffset.UtcNow.AddMinutes(10);
            while (DateTimeOffset.UtcNow < until && !page.IsClosed && hub.IsConnected(runnerId))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    var (status, detail) = await ReadAsync(page);
                    if (status == "signedIn")
                    {
                        await SaveAsync(runnerId, domain, status, detail, siteId, url);
                        return;
                    }
                }
                catch (Exception)
                {
                    // Mid-navigation, or the tab was closed: look again.
                }
            }
        });
    }
}

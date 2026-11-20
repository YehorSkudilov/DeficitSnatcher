using System.Collections.Concurrent;
using System.Text.Json;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Pc;

public sealed record StoreLoginDto(string Domain, string Status, DateTimeOffset? CheckedAt, string? Detail, string AccountUrl, bool Paused = false,
    DateTimeOffset? RetryAt = null);

/// <summary>
/// Which stores each PC helper's Chrome is signed in to: it opens the store's account page in that Chrome now and then (when
/// the helper connects, then every few hours) and looks: a sign-in form means signed out, a bot check means the owner has to
/// prove they're human. Either sends an alert, once, so the owner can sign in from the Helpers tab (which opens the store's
/// page in that Chrome, in front). It never types a password.
/// </summary>
public sealed class LoginWatcher(IServiceScopeFactory scopes, RunnerHub hub, BrowserPool browsers, Notifier notifier,
    Settings.SettingsStore settingsStore, ILogger<LoginWatcher> log) : BackgroundService
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
        var cooldown = TimeSpan.FromMinutes((await settingsStore.GetAsync()).AutoSignInCooldownMinutes);
        return stores.OrderBy(s => s.Key).Select(s => rows.TryGetValue(s.Key, out var r)
                ? new StoreLoginDto(s.Key, r.Status, r.CheckedAt, r.Detail, AccountUrl(s.Key, s.Value.Url), hub.IsPaused(runnerId, s.Key),
                    hub.IsPaused(runnerId, s.Key) && _autoTried.TryGetValue((runnerId, s.Key), out var at) ? at + cooldown : (DateTimeOffset?)null)
                : new StoreLoginDto(s.Key, "unknown", null, null, AccountUrl(s.Key, s.Value.Url)))
            .ToList();
    }

    /// <summary>Which helpers sit which stores out (signed out of a store you saved a sign-in for): from what was last seen.
    /// At start, and when a saved sign-in is added or removed.</summary>
    public async Task RefreshPausedAsync()
    {
        using var scope = scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RunnerLogins.AsNoTracking().ToListAsync();
        foreach (var r in rows)
            hub.SetPaused(r.RunnerId, r.Domain, await ShouldPauseAsync(r.Status, r.Domain));
    }

    private async Task<bool> ShouldPauseAsync(string status, string domain) =>
        status is "signedOut" or "humanCheck" && await AccountForAsync(domain) != null;

    private async Task RetryPausedAsync(CancellationToken stop)
    {
        using var scope = scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RunnerLogins.AsNoTracking()
            .Where(l => l.Status == "signedOut" || l.Status == "humanCheck").ToListAsync(stop);
        var cooldown = TimeSpan.FromMinutes((await settingsStore.GetAsync(stop)).AutoSignInCooldownMinutes);
        foreach (var r in rows)
            if (hub.IsConnected(r.RunnerId) && hub.IsPaused(r.RunnerId, r.Domain)
                && (!_autoTried.TryGetValue((r.RunnerId, r.Domain), out var at) || DateTimeOffset.UtcNow - at >= cooldown))
                await CheckAllAsync(r.RunnerId, stop, r.Domain);
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
            await RefreshPausedAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), stop);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stop.IsCancellationRequested)
        {
            // Stores a helper sits out (signed out): signed in again by itself as soon as the cooldown since its last try is over.
            try
            {
                await RetryPausedAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogWarning("Couldn't retry signing in: {Error}", e.Message);
            }
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
                if (!hub.IsConnected(runnerId) || hub.IsSigningIn(runnerId))
                    return;
                var url = AccountUrl(domain, site.Url);
                var (status, detail) = await CheckOneAsync(runnerId, url, ct);
                // Signed out, and you saved your sign-in for this store: it signs in (at most every 6 hours by itself, so a
                // wrong password can't lock the account).
                if (status == "signedOut" && await AccountForAsync(domain) != null && await AutoAttemptAllowedAsync(runnerId, domain))
                {
                    await SignInAsync(runnerId, domain, ct);
                    continue;
                }
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
            page = await BrowserPool.OpenPcPageAsync(context, newWindow: false);
            var (status, detail) = await VisitAsync(page, url, ct);
            // The account page didn't settle it (not found, or nothing to tell by): the store's home page, whose header
            // usually says who's signed in.
            if (status == "unknown" && Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var (homeStatus, homeDetail) = await VisitAsync(page, u.GetLeftPart(UriPartial.Authority) + "/", ct);
                if (homeStatus != "unknown")
                    return (homeStatus, homeDetail);
            }
            return (status, detail);
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

    private static async Task<(string Status, string? Detail)> VisitAsync(IPage page, string url, CancellationToken ct)
    {
        IResponse? response = null;
        try
        {
            response = await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
        }
        catch (TimeoutException)
        {
        }
        await Task.Delay(TimeSpan.FromSeconds(4), ct); // redirects to sign in, scripts drawing the header
        var read = await ReadAsync(page);
        if (read.Status == "unknown" && response is { Status: >= 400 } r)
            return ("unknown", $"The account page answered {r.Status}.");
        return read;
    }

    /// <summary>What the page's links say about signing in, gathered in the page.</summary>
    private const string EvidenceScript = """
        () => {
          const text = e => (e.innerText || e.textContent || '').replace(/\s+/g, ' ').trim();
          const els = [...document.querySelectorAll('a, button, [role=button], [role=menuitem]')];
          const out = els.find(e => /^(sign|log)\s?out\b/i.test(text(e)) || /(sign|log)[-_]?out/i.test(e.getAttribute('href') || ''));
          const signIn = els.find(e => /^(hello,?\s*)?(sign|log)\s?in\b/i.test(text(e)) || /^(sign|log)\s?in\s*(\/|or)\s*(register|create)/i.test(text(e)));
          const amazon = document.querySelector('#nav-link-accountList-nav-line-1');
          return { out: out ? (text(out) || out.getAttribute('href')) : null, signIn: signIn ? text(signIn) : null, greeting: amazon ? text(amazon) : null };
        }
        """;

    private sealed record Evidence(string? Out, string? SignIn, string? Greeting);

    /// <summary>
    /// What an open store page says about signing in. "Signed in" needs proof (a way to sign out, or Amazon's "Hello, name");
    /// a page that says neither way is "unknown", never taken as signed in.
    /// </summary>
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

        Evidence? e = null;
        try
        {
            var j = await page.EvaluateAsync<System.Text.Json.JsonElement>(EvidenceScript);
            static string? Get(System.Text.Json.JsonElement j, string name) =>
                j.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            e = new Evidence(Get(j, "out"), Get(j, "signIn"), Get(j, "greeting"));
        }
        catch (Exception x) when (x is PlaywrightException or InvalidOperationException or System.Text.Json.JsonException)
        {
        }
        if (e?.Greeting is { Length: > 0 } g)
            return g.Contains("sign in", StringComparison.OrdinalIgnoreCase)
                ? ("signedOut", $"The page says \"{Cut(g)}\".")
                : ("signedIn", $"The page says \"{Cut(g)}\".");
        if (e?.Out != null)
            return ("signedIn", $"The page offers \"{Cut(e.Out)}\".");
        if (e?.SignIn != null)
            return ("signedOut", $"The page offers \"{Cut(e.SignIn)}\".");
        return ("unknown", "Couldn't tell from the page: press Log in to look.");
    }

    private static string Cut(string s) => s.Length > 80 ? s[..80] + "…" : s;

    private async Task SaveAsync(int runnerId, string domain, string status, string? detail, int siteId, string? pageUrl, bool alert = true)
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
        var paused = await ShouldPauseAsync(status, domain);
        hub.SetPaused(runnerId, domain, paused);

        if (status == before || !alert)
            return;
        var name = hub.NameOf(runnerId);
        var link = siteId > 0 ? notifier.Link($"site/{siteId}") : notifier.Link("helpers");
        if (status == "signedOut")
            await notifier.NotifyAsync(NotifyKind.SignedOut, EventLevel.Warning,
                $"{name} isn't signed in to {domain}. "
                + (paused ? $"It sits {domain} out until it's signed in, and tries your saved sign-in again by itself after the cooldown. " : "")
                + $"To sign in now: Log in next to {domain} on its product's page, and the store opens in that computer's Chrome.",
                siteId, AccountUrl(domain, pageUrl), link);
        else if (status == "humanCheck")
            await notifier.NotifyAsync(NotifyKind.HumanCheck, EventLevel.Warning,
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
        if (hub.CanShow(runnerId))
        {
            // The helper opens it in a window of its own (one per store).
            await hub.ShowAsync(runnerId, url);
        }
        else
        {
            // A window of its own, in front: the bot's tabs open in the background and never take it over while you sign in.
            var page = await BrowserPool.OpenPcPageAsync(context, newWindow: true);
            await HumanCheck.ShowAsync(page);
            try
            {
                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            }
            catch (TimeoutException)
            {
            }
            await HumanCheck.ShowAsync(page);
        }
        _ = WatchSignInAsync(runnerId, domain, s.SiteId, url);
    }

    /// <summary>
    /// For ten minutes after Log in, looks at the helper's tabs on that store (whichever Chrome is up: it may have restarted
    /// with a window) and notes as soon as one proves you're signed in.
    /// </summary>
    private async Task WatchSignInAsync(int runnerId, string domain, int siteId, string url)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!hub.IsConnected(runnerId))
                continue;
            try
            {
                var context = await browsers.GetPcContextAsync(runnerId, CancellationToken.None);
                foreach (var page in context.Pages.ToList())
                {
                    if (page.IsClosed || Domain(page.Url) != domain)
                        continue;
                    var (status, detail) = await ReadAsync(page);
                    if (status == "signedIn")
                    {
                        await SaveAsync(runnerId, domain, status, detail, siteId, url);
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Mid-navigation, a tab closing, Chrome restarting: look again.
            }
        }
    }

    // ---- Signing in with your saved sign-in ----

    private readonly ConcurrentDictionary<(int, string), DateTimeOffset> _autoTried = new();

    /// <summary>Whether it may try your saved sign-in by itself now (Settings → auto sign-in cooldown since its last try);
    /// it notes the try.</summary>
    private async Task<bool> AutoAttemptAllowedAsync(int runnerId, string domain)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromMinutes((await settingsStore.GetAsync()).AutoSignInCooldownMinutes);
        if (_autoTried.TryGetValue((runnerId, domain), out var at) && now - at < cooldown)
            return false;
        _autoTried[(runnerId, domain)] = now;
        return true;
    }

    private async Task<Settings.StoreAccount?> AccountForAsync(string domain) =>
        (await settingsStore.GetStoreAccountsAsync()).FirstOrDefault(a => a.Domain == domain || domain.EndsWith("." + a.Domain, StringComparison.Ordinal));

    private const string UserFields =
        "input[type=email]:visible, input[autocomplete=username]:visible, input[autocomplete=email]:visible, input[name*=email i]:visible, "
        + "input[id*=email i]:visible, input[name*=user i]:visible, input[name*=login i]:visible";

    private const string CodeFields =
        "input[autocomplete=one-time-code]:visible, input[name*=otp i]:visible, input[name*=code i]:visible, input[id*=code i]:visible";

    /// <summary>
    /// Signs the helper's Chrome in to the store with your saved sign-in: the store's sign-in page, your email, then your
    /// password, like a password manager filling them in. It stops at anything that's yours to do (a captcha, a code sent to
    /// you, a wrong password): that page opens in a window for you and you get an alert.
    /// </summary>
    public async Task SignInAsync(int runnerId, string domain, CancellationToken ct)
    {
        var stores = await StoresAsync();
        stores.TryGetValue(domain, out var site);
        var account = await AccountForAsync(domain);
        if (account == null)
            return;
        // Where to start: the sign-in page you gave, else the one it last signed in on, else the store's account page.
        var url = account.SignInUrl ?? account.LearnedSignInUrl ?? AccountUrl(domain, site.Url);
        var name = hub.NameOf(runnerId);
        var (ok, detail, stoppedAt, formUrl) = await FillSignInAsync(runnerId, domain, site.SiteId, url, account, ct);
        if (ok && account.SignInUrl == null && formUrl != null && formUrl != account.LearnedSignInUrl)
            await LearnSignInPageAsync(account, formUrl);
        if (ok)
        {
            await SaveAsync(runnerId, domain, "signedIn", detail, site.SiteId, site.Url);
            await notifier.LogAsync(EventLevel.Good, $"{name} signed in to {domain} with your saved sign-in.", site.SiteId);
            return;
        }
        await SaveAsync(runnerId, domain, detail.Contains("human", StringComparison.OrdinalIgnoreCase) ? "humanCheck" : "signedOut", detail,
            site.SiteId, site.Url, alert: false);
        var shown = await hub.ShowAsync(runnerId, stoppedAt ?? url);
        await notifier.NotifyAsync(NotifyKind.SignedOut, EventLevel.Warning,
            $"{name} couldn't sign in to {domain} by itself: {detail} "
            + (shown ? "The sign-in page is open in a window on that computer: finish it there." : "On the Helpers tab press Log in next to it to finish."),
            site.SiteId, url, notifier.Link("helpers"));
    }

    /// <summary>Signs in; FormUrl is the page the password went in on (remembered for next time).</summary>
    private async Task<(bool Ok, string Detail, string? StoppedAt, string? FormUrl)> FillSignInAsync(int runnerId, string domain, int siteId,
        string url, Settings.StoreAccount account, CancellationToken ct)
    {
        IPage? page = null;
        try
        {
            var context = await browsers.GetPcContextAsync(runnerId, ct);
            page = await BrowserPool.OpenPcPageAsync(context, newWindow: false);
            try
            {
                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            }
            catch (TimeoutException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            string? formUrl = null;
            var looked = 0;
            var passwordSent = false;
            var humanAsked = false;
            var codesSent = 0;
            for (var step = 0; step < 8; step++)
            {
                var (status, detail) = await ReadAsync(page);
                if (status == "signedIn")
                    return (true, detail ?? "Signed in.", null, formUrl);
                if (status == "humanCheck")
                {
                    // Yours to do: the page comes up on that computer, you're alerted, and it carries on as soon as it's done.
                    if (humanAsked || !await WaitForHumanAsync(runnerId, domain, siteId, page, ct))
                        return (false, "the store wants you to prove you're human, and it wasn't done within "
                            + $"{AskFor.TotalMinutes:0} minutes.", page.Url, formUrl);
                    humanAsked = true;
                    continue;
                }
                if (await Visible(page, CodeFields))
                {
                    // A code the store sent you (text, email, app): you type it on the site, it goes in here.
                    if (codesSent >= 2)
                        return (false, "the store didn't take the code.", page.Url, formUrl);
                    var code = await AskCodeAsync(runnerId, domain, siteId, page, ct);
                    if (code == null)
                        return (false, $"the store sent you a code and none was entered within {AskFor.TotalMinutes:0} minutes.", page.Url, formUrl);
                    var field = page.Locator(CodeFields).First;
                    await field.FillAsync(code);
                    await field.PressAsync("Enter");
                    codesSent++;
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                var password = page.Locator("input[type=password]:visible").First;
                var user = page.Locator(UserFields).First;
                var hasPassword = await Visible(page, "input[type=password]:visible");
                if (hasPassword && passwordSent)
                    return (false, "it still asks for the password (is the saved one right?).", page.Url, formUrl);
                if (await Visible(page, UserFields) && string.IsNullOrEmpty(await user.InputValueAsync()))
                    await user.FillAsync(account.Email);
                var before = page.Url;
                if (hasPassword)
                {
                    formUrl = page.Url;
                    await password.FillAsync(account.Password);
                    await password.PressAsync("Enter");
                    passwordSent = true;
                }
                else if (await Visible(page, UserFields))
                    await user.PressAsync("Enter");
                else if (looked < 3 && await FindSignInAsync(page, domain, siteId, looked++, ct))
                {
                    // Went to (or opened) where it signs in: look again.
                }
                else
                    return (false, "couldn't find where to sign in (give its sign-in page with your saved sign-in, on the product's page).",
                        page.Url, formUrl);

                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                // Enter didn't go anywhere: the form's own button.
                if (page.Url == before && !passwordSent && !await Visible(page, "input[type=password]:visible")
                    && page.Locator("button[type=submit]:visible, input[type=submit]:visible").First is { } submit && await submit.CountAsync() > 0)
                {
                    await submit.ClickAsync();
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                }
            }
            var (final, finalDetail) = await ReadAsync(page);
            return final == "signedIn" ? (true, finalDetail ?? "Signed in.", null, formUrl) : (false, "it didn't get signed in.", page.Url, formUrl);
        }
        catch (PcHelperException e)
        {
            return (false, e.Message, null, null);
        }
        catch (PlaywrightException e)
        {
            return (false, e.Message.Split('\n')[0], page?.Url, null);
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

    // ---- Finding where a store signs in ----

    private static readonly System.Text.RegularExpressions.Regex SignInText = new(
        @"^\s*(sign|log)\s?-?\s?(in|on)\b|^\s*login\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex AccountText = new(
        @"\b(sign|log)\s?-?\s?(in|on)\b|\blogin\b|\bmy account\b|^\s*account\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private const string SignInLinks =
        "a[href*=login i]:visible, a[href*=signin i]:visible, a[href*=sign-in i]:visible, a[href*=logon i]:visible, a[href*=log-in i]:visible";

    /// <summary>
    /// The page has no sign-in form: goes where it signs in. First by what's on this page (a "Sign in" / "Log in" / "My account"
    /// link or button, or a link to a login address), then from the store's home page, then by asking the AI which of the
    /// page's links and buttons leads to signing in. True when it went somewhere.
    /// </summary>
    private async Task<bool> FindSignInAsync(IPage page, string domain, int siteId, int attempt, CancellationToken ct)
    {
        if (attempt == 1 && Uri.TryCreate(page.Url, UriKind.Absolute, out var here))
        {
            try
            {
                await page.GotoAsync(here.GetLeftPart(UriPartial.Authority) + "/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            }
            catch (TimeoutException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        if (attempt < 2)
        {
            foreach (var regex in new[] { SignInText, AccountText })
            {
                var target = page.Locator("a:visible, button:visible, [role=button]:visible, [role=link]:visible").Filter(new() { HasTextRegex = regex }).First;
                if (await target.CountAsync() > 0)
                {
                    await target.ClickAsync(new() { Timeout = 10000 });
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                    return true;
                }
            }
            var link = page.Locator(SignInLinks).First;
            if (await link.CountAsync() > 0)
            {
                await link.ClickAsync(new() { Timeout = 10000 });
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                return true;
            }
            // Nothing here: from the home page next.
            return attempt == 0;
        }
        return await AskAiForSignInAsync(page, domain, siteId, ct);
    }

    /// <summary>The page's links and buttons go to the AI, which says which one leads to signing in.</summary>
    private async Task<bool> AskAiForSignInAsync(IPage page, string domain, int siteId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var ai = scope.ServiceProvider.GetRequiredService<Ai.OpenAiClient>();
        if (!await ai.AvailableAsync(ct))
            return false;
        var items = await page.EvaluateAsync<JsonElement>("""
            () => [...document.querySelectorAll('a, button, [role=button], [role=link]')]
              .filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; })
              .slice(0, 150)
              .map((e, i) => ({ i, text: (e.innerText || e.getAttribute('aria-label') || e.title || '').trim().slice(0, 60), href: e.getAttribute('href') || '' }))
            """);
        var list = new System.Text.StringBuilder();
        foreach (var item in items.EnumerateArray())
            list.AppendLine($"{item.GetProperty("i").GetInt32()}: \"{item.GetProperty("text").GetString()}\" {item.GetProperty("href").GetString()}");
        JsonElement answer;
        try
        {
            answer = await ai.AskJsonAsync("find-sign-in", siteId > 0 ? siteId : null, null,
                "You help a browser sign in to an online store (" + domain + ") for its owner. You get the current page's address and its "
                + "visible links and buttons (index: \"text\" href). Pick the one that leads to the sign-in page or opens the sign-in form "
                + "(often \"Sign in\", \"Log in\", \"Account\", a person icon, \"My account\"; not \"Create account\" or \"Sign up\"). If "
                + "none does but you know the store's sign-in address, give it. Answer JSON: {\"index\": number or null, \"url\": string or "
                + "null, \"why\": short}.",
                [Ai.OpenAiClient.Text($"Page: {page.Url}\nTitle: {await page.TitleAsync()}\n\n{list}")], ct);
        }
        catch (Ai.OpenAiClient.AiException e)
        {
            log.LogInformation("AI couldn't help find {Domain}'s sign-in: {Error}", domain, e.Message);
            return false;
        }
        if (answer.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number)
        {
            var el = page.Locator("a, button, [role=button], [role=link]").Filter(new() { Visible = true }).Nth(index.GetInt32());
            if (await el.CountAsync() > 0)
            {
                await el.ClickAsync(new() { Timeout = 10000 });
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                return true;
            }
        }
        if (answer.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } u
            && Uri.TryCreate(new Uri(page.Url), u, out var target) && target.Scheme is "http" or "https" && Domain(target.ToString()) == domain)
        {
            await page.GotoAsync(target.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return true;
        }
        return false;
    }

    /// <summary>Remembers the page it signed in on, to start there next time.</summary>
    private async Task LearnSignInPageAsync(Settings.StoreAccount account, string url)
    {
        var all = await settingsStore.GetStoreAccountsAsync();
        var i = all.FindIndex(a => a.Domain == account.Domain);
        if (i < 0)
            return;
        all[i] = all[i] with { LearnedSignInUrl = url };
        await settingsStore.SaveStoreAccountsAsync(all);
    }

    // ---- Waiting for you: a code the store sent you, or proving you're human ----

    /// <summary>How long a sign-in waits for you (a code, a captcha) before it gives up.</summary>
    public static readonly TimeSpan AskFor = TimeSpan.FromMinutes(5);

    public sealed record PendingCodeDto(int RunnerId, string Helper, string Domain, DateTimeOffset Since, DateTimeOffset Until);

    private sealed record PendingCode(PendingCodeDto Info, TaskCompletionSource<string> Code);

    private readonly ConcurrentDictionary<(int, string), PendingCode> _codes = new();

    /// <summary>Sign-ins waiting for a code you got from the store.</summary>
    public List<PendingCodeDto> PendingCodes() => _codes.Values.Select(p => p.Info).OrderBy(p => p.Since).ToList();

    /// <summary>The code for a waiting sign-in; false when nothing is waiting for it (it gave up, or it's done).</summary>
    public bool SubmitCode(int runnerId, string domain, string code) =>
        _codes.TryGetValue((runnerId, domain), out var p) && p.Code.TrySetResult(code.Trim());

    private async Task<string?> AskCodeAsync(int runnerId, string domain, int siteId, IPage page, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new PendingCode(new PendingCodeDto(runnerId, hub.NameOf(runnerId), domain, now, now + AskFor),
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        _codes[(runnerId, domain)] = pending;
        try
        {
            await notifier.NotifyAsync(NotifyKind.SignedOut, EventLevel.Warning,
                $"{hub.NameOf(runnerId)} is signing in to {domain} and the store sent you a verification code: enter it on the site "
                + $"(it waits {AskFor.TotalMinutes:0} minutes).", siteId, null, notifier.Link("dashboard"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AskFor);
            // Its page gone (the helper restarted, or the tab was closed): nothing to type it into, so it stops asking.
            void closed(object? _, IPage __) => timeout.Cancel();
            page.Close += closed;
            try
            {
                if (page.IsClosed)
                    return null;
                return await pending.Code.Task.WaitAsync(timeout.Token);
            }
            finally
            {
                page.Close -= closed;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _codes.TryRemove(new KeyValuePair<(int, string), PendingCode>((runnerId, domain), pending));
        }
    }

    /// <summary>The page comes up on that computer and you're alerted; true once it's the store again.</summary>
    private async Task<bool> WaitForHumanAsync(int runnerId, string domain, int siteId, IPage page, CancellationToken ct)
    {
        await HumanCheck.ShowAsync(page);
        await notifier.NotifyAsync(NotifyKind.HumanCheck, EventLevel.Warning,
            $"{hub.NameOf(runnerId)} is signing in to {domain} and the store wants you to prove you're human: do it in the Chrome window "
            + $"that came up on that computer. It carries on by itself once it's done (it waits {AskFor.TotalMinutes:0} minutes).", siteId);
        var until = DateTimeOffset.UtcNow + AskFor;
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            try
            {
                if ((await ReadAsync(page)).Status != "humanCheck")
                    return true;
            }
            catch (PlaywrightException)
            {
                // Mid-navigation: look again.
            }
        }
        return false;
    }

    private static async Task<bool> Visible(IPage page, string selector)
    {
        try
        {
            return await page.Locator(selector).CountAsync() > 0;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }
}

using System.Collections.Concurrent;
using DeficitSnatcher.Api.Data;

namespace DeficitSnatcher.Api.Pc;

/// <summary>
/// Who does each check: the connected PC helpers and the server, taking turns page by page, so a page's checks are spread
/// over all of them and never two at once. Helpers first: each gets two turns for the server's one. Pages set to "Run on my
/// PC" leave the server out while any helper is connected (those stores block it). A helper signed out of a store you saved a
/// sign-in for sits that store out until it's signed in again.
/// Workers are named "server" or "pc:{helper id}".
/// </summary>
public sealed class WorkerPool(RunnerHub hub)
{
    public const string Server = "server";
    private readonly ConcurrentDictionary<int, int> _turn = new();

    public static string Pc(int id) => $"pc:{id}";

    public static int? PcId(string? worker) =>
        worker != null && worker.StartsWith("pc:", StringComparison.Ordinal) && int.TryParse(worker[3..], out var id) ? id : null;

    // ---- Backing off: a store that blocked a worker isn't asked again from there for a while ----

    private readonly ConcurrentDictionary<(int Site, string Worker), (int Strikes, DateTimeOffset Until)> _backoff = new();

    /// <summary>The store blocked this page's check from this worker: it rests there for 15 minutes, doubling with each block
    /// in a row (up to 6 hours, a little random), so a flag can wear off instead of being confirmed every minute. Returns
    /// how long.</summary>
    public TimeSpan Blocked(int siteId, string worker)
    {
        var strikes = _backoff.TryGetValue((siteId, worker), out var b) ? b.Strikes + 1 : 1;
        var minutes = Math.Min(15 * Math.Pow(2, strikes - 1), 360) * (0.85 + Random.Shared.NextDouble() * 0.3);
        var rest = TimeSpan.FromMinutes(minutes);
        _backoff[(siteId, worker)] = (strikes, DateTimeOffset.UtcNow + rest);
        return rest;
    }

    /// <summary>A check went through: no more resting there.</summary>
    public void Succeeded(int siteId, string worker) => _backoff.TryRemove((siteId, worker), out _);

    private bool Resting(int siteId, string worker) =>
        _backoff.TryGetValue((siteId, worker), out var b) && b.Until > DateTimeOffset.UtcNow;

    /// <summary>
    /// Why this page isn't checked right now, or null when it is: every worker that would check it is resting after the
    /// store blocked it, or (Run on my PC) its helpers are connected but busy for a moment (you're signing in on one, or it's
    /// signing back in to the store), where the server would only be blocked.
    /// </summary>
    public string? Waits(Site site)
    {
        if (Order(site).Count > 0)
            return null;
        var domain = LoginWatcher.Domain(site.Url);
        var rests = _backoff.Where(kv => kv.Key.Site == site.Id && kv.Value.Until > DateTimeOffset.UtcNow).Select(kv => kv.Value.Until).ToList();
        if (rests.Count > 0)
            return $"Resting: {domain} blocked the last checks, so it isn't asked again until {rests.Min():HH:mm} UTC (blocks wear off with "
                + "time; checking again right away keeps them on). Check now still checks.";
        var helpers = hub.ConnectedIds.Where(id => hub.Allowed(id, domain)).ToList();
        if (helpers.Count > 0 && hub.IsPaused(helpers[0], domain))
            return $"Waiting for {hub.NameOf(helpers[0])} to sign back in to {domain} (it signs in by itself; or Log in on this product's page).";
        return helpers.Count > 0 ? $"Waiting for {hub.NameOf(helpers[0])}: a sign-in window is open on it (a few minutes at most)." : null;
    }

    /// <summary>The worker for this page's next check (its turn moves on). With every worker resting (a manual check), the
    /// usual order regardless.</summary>
    public string Next(Site site)
    {
        var order = Order(site);
        if (order.Count == 0)
            order = Order(site, ignoreRest: true);
        var i = _turn.AddOrUpdate(site.Id, 0, (_, t) => t + 1);
        return order[i % order.Count];
    }

    /// <summary>The best worker for a job that isn't a turn (a checkout, setting a page up): a helper when one's connected
    /// (the first, or <paramref name="preferred"/> if it still is), else the server.</summary>
    public string Best(Site? site, string? preferred = null)
    {
        var domain = site == null ? null : LoginWatcher.Domain(site.Url);
        bool ok(int id) => !hub.IsSigningIn(id) && (domain == null || hub.Allowed(id, domain) && !hub.IsPaused(id, domain));
        if (PcId(preferred) is { } p && hub.IsConnected(p) && ok(p))
            return preferred!;
        if (preferred == Server && site?.RunOnPc != true)
            return Server;
        var connected = hub.ConnectedIds.Where(ok).ToList();
        return connected.Count > 0 ? Pc(connected[0]) : Server;
    }

    /// <summary>Is this worker usable now (a helper that's connected, or the server)?</summary>
    public bool Available(string worker) => worker == Server || PcId(worker) is { } id && hub.IsConnected(id);

    public string Label(string? worker) => PcId(worker) is { } id ? hub.NameOf(id) : "Server";

    /// <summary>One round of turns: each helper twice (spread out), then the server once. Helpers switched off (for
    /// everything or for this store) sit it out; with none left, the server does them all.</summary>
    private List<string> Order(Site site, bool ignoreRest = false)
    {
        var domain = LoginWatcher.Domain(site.Url);
        var connected = hub.ConnectedIds.Where(id => hub.Allowed(id, domain)).ToList();
        var helpers = connected.Where(id => !hub.IsSigningIn(id) && !hub.IsPaused(id, domain)).Select(Pc)
            .Where(w => ignoreRest || !Resting(site.Id, w)).ToList();
        var order = new List<string>();
        order.AddRange(helpers);
        // Run on my PC: the server only when no helper is connected at all (one that's busy or resting is waited for).
        if (!(site.RunOnPc && connected.Count > 0) && (ignoreRest || !Resting(site.Id, Server)))
            order.Add(Server);
        order.AddRange(helpers);
        return order;
    }
}

using System.Collections.Concurrent;
using DeficitSnatcher.Api.Data;

namespace DeficitSnatcher.Api.Pc;

/// <summary>
/// Who does each check: the connected PC helpers and the server, taking turns page by page, so a page's checks are spread
/// over all of them and never two at once. Helpers first: each gets two turns for the server's one. Pages set to "Run on my
/// PC" leave the server out while any helper is connected (those stores block it).
/// Workers are named "server" or "pc:{helper id}".
/// </summary>
public sealed class WorkerPool(RunnerHub hub)
{
    public const string Server = "server";
    private readonly ConcurrentDictionary<int, int> _turn = new();

    public static string Pc(int id) => $"pc:{id}";

    public static int? PcId(string? worker) =>
        worker != null && worker.StartsWith("pc:", StringComparison.Ordinal) && int.TryParse(worker[3..], out var id) ? id : null;

    /// <summary>The worker for this page's next check (its turn moves on).</summary>
    public string Next(Site site)
    {
        var order = Order(site);
        var i = _turn.AddOrUpdate(site.Id, 0, (_, t) => t + 1);
        return order[i % order.Count];
    }

    /// <summary>The best worker for a job that isn't a turn (a checkout, setting a page up): a helper when one's connected
    /// (the first, or <paramref name="preferred"/> if it still is), else the server.</summary>
    public string Best(Site? site, string? preferred = null)
    {
        var domain = site == null ? null : LoginWatcher.Domain(site.Url);
        bool ok(int id) => domain == null || hub.Allowed(id, domain);
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
    private List<string> Order(Site site)
    {
        var domain = LoginWatcher.Domain(site.Url);
        var helpers = hub.ConnectedIds.Where(id => hub.Allowed(id, domain)).Select(Pc).ToList();
        var order = new List<string>();
        order.AddRange(helpers);
        if (!(site.RunOnPc && helpers.Count > 0))
            order.Add(Server);
        order.AddRange(helpers);
        return order;
    }
}

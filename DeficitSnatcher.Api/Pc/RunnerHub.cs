using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Pc;

/// <summary>
/// The PC helpers (DeficitSnatcher.Runner) on the owner's computers: each connects out to the site with its own key and
/// lends it its Chrome.
///
/// A helper keeps a control WebSocket open (/api/runner/connect). When the server wants a helper's browser, Playwright
/// connects to a loopback-only endpoint here (/internal/cdp/{secret}/{helper}); the server asks that helper over its
/// control socket to open a pipe (/api/runner/pipe/{id}), and the helper joins the pipe to its Chrome's DevTools socket.
/// From then on Playwright drives that Chrome as if it were local. Nothing on the PCs listens to the network.
/// </summary>
public sealed class RunnerHub(IServiceScopeFactory scopes, Notifier notifier, ILogger<RunnerHub> log)
{
    private const string LegacyKeyName = "runner-key-hash";

    /// <summary>The loopback endpoint's secret path part, new each start: only this process knows it.</summary>
    public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private sealed class Live(int id, string name, WebSocket control, string? machine, string? browser, bool canShow, bool canUpdate, string? version)
    {
        /// <summary>The helper updates itself when asked (newer helpers).</summary>
        public bool CanUpdate { get; } = canUpdate;
        public string? Version { get; } = version;
        /// <summary>The owner is signing in (or proving they're human) in a window of this helper's: it gets no work meanwhile.</summary>
        public volatile bool SigningIn;
        /// <summary>The helper can open a page in a window of its own, in front (newer helpers).</summary>
        public bool CanShow { get; } = canShow;
        public int Id { get; } = id;
        public string Name { get; set; } = name;
        public WebSocket Control { get; } = control;
        public SemaphoreSlim Send { get; } = new(1, 1);
        public string? Machine { get; } = machine;
        public string? Browser { get; } = browser;
        public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<int, Live> _live = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocket>> _waitingPipes = new();
    private readonly ConcurrentDictionary<WebSocket, TaskCompletionSource> _pipeDone = new();

    public sealed record Info(int Id, string Name, bool Connected, string? Machine, string? Browser, DateTimeOffset? Since,
        DateTimeOffset? LastSeen, DateTimeOffset CreatedAt, bool ChecksEnabled, List<string> DisabledDomains, string? Version = null,
        bool CanUpdate = false);

    /// <summary>What each helper is allowed to do (its switches on the Helpers tab), kept here so turns are quick to work out.</summary>
    private sealed record Prefs(bool ChecksEnabled, HashSet<string> Off);
    private readonly ConcurrentDictionary<int, Prefs> _prefs = new();

    private static Prefs PrefsOf(RunnerKey k) =>
        new(k.ChecksEnabled, k.DisabledDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>May this helper check and buy at this store (domain, e.g. walmart.ca)?</summary>
    public bool Allowed(int id, string domain) =>
        !_prefs.TryGetValue(id, out var p) || p.ChecksEnabled && !p.Off.Contains(domain);

    /// <summary>Turns a helper's checks on or off, all of them or one store's.</summary>
    public async Task<bool> SetChecksAsync(int id, bool on, string? domain)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.RunnerKeys.FirstOrDefaultAsync(r => r.Id == id);
        if (row == null)
            return false;
        if (string.IsNullOrWhiteSpace(domain))
            row.ChecksEnabled = on;
        else
        {
            var off = PrefsOf(row).Off;
            if (on)
                off.Remove(domain.Trim());
            else
                off.Add(domain.Trim().ToLowerInvariant());
            row.DisabledDomains = string.Join(",", off.Order());
        }
        await db.SaveChangesAsync();
        _prefs[id] = PrefsOf(row);
        return true;
    }

    /// <summary>The helpers connected now, oldest key first.</summary>
    public IReadOnlyList<int> ConnectedIds =>
        _live.Values.Where(r => r.Control.State == WebSocketState.Open).Select(r => r.Id).OrderBy(i => i).ToList();

    public bool Connected => ConnectedIds.Count > 0;
    public bool IsConnected(int id) => _live.TryGetValue(id, out var r) && r.Control.State == WebSocketState.Open;

    /// <summary>Is the owner using a window of this helper's Chrome (sign in, prove you're human)? It's left alone then.</summary>
    public bool IsSigningIn(int id) => _live.TryGetValue(id, out var r) && r.SigningIn;
    public string NameOf(int id) => _live.TryGetValue(id, out var r) ? r.Name : $"PC helper {id}";

    /// <summary>Raised with the helper's id when it connects or goes away (the browser pool drops its connection).</summary>
    public event Action<int>? Changed;

    // ---- Keys: one per helper ----

    /// <summary>A key for a new helper (shown once, as part of its setup code).</summary>
    public async Task<(int Id, string Key)> NewKeyAsync(string? name)
    {
        var key = "dsr_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.RunnerKeys.CountAsync();
        var row = new RunnerKey
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"PC helper {count + 1}" : name.Trim()[..Math.Min(name.Trim().Length, 100)],
            KeyHash = Hash(key),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RunnerKeys.Add(row);
        await db.SaveChangesAsync();
        return (row.Id, key);
    }

    /// <summary>The helper a key belongs to, or null.</summary>
    public async Task<RunnerKey?> FindAsync(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        await MoveLegacyKeyAsync();
        var hash = Hash(key);
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().RunnerKeys.AsNoTracking().FirstOrDefaultAsync(r => r.KeyHash == hash);
    }

    /// <summary>Every helper there's a key for, connected or not.</summary>
    public async Task<List<Info>> ListAsync()
    {
        await MoveLegacyKeyAsync();
        using var scope = scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RunnerKeys.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        return rows.Select(r => _live.TryGetValue(r.Id, out var l) && l.Control.State == WebSocketState.Open
                ? new Info(r.Id, r.Name, true, l.Machine, l.Browser, l.Since, l.LastSeen, r.CreatedAt, r.ChecksEnabled, [.. PrefsOf(r).Off], l.Version, l.CanUpdate)
                : new Info(r.Id, r.Name, false, r.Machine, r.Browser, null, r.LastSeenAt, r.CreatedAt, r.ChecksEnabled, [.. PrefsOf(r).Off]))
            .ToList();
    }

    public async Task<bool> RenameAsync(int id, string name)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.RunnerKeys.FirstOrDefaultAsync(r => r.Id == id);
        if (row == null || string.IsNullOrWhiteSpace(name))
            return false;
        row.Name = name.Trim()[..Math.Min(name.Trim().Length, 100)];
        await db.SaveChangesAsync();
        if (_live.TryGetValue(id, out var l))
            l.Name = row.Name;
        return true;
    }

    /// <summary>The helper's key stops working, and it's disconnected now.</summary>
    public async Task<bool> RevokeAsync(int id)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gone = await db.RunnerKeys.Where(r => r.Id == id).ExecuteDeleteAsync() > 0;
        if (_live.TryRemove(id, out var l))
            await CloseAsync(l.Control, "This helper's setup code was revoked.");
        Changed?.Invoke(id);
        return gone;
    }

    /// <summary>The first version had one key, kept as a setting: it becomes the first helper.</summary>
    private async Task MoveLegacyKeyAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var legacy = await db.StoredValues.FirstOrDefaultAsync(v => v.Key == LegacyKeyName);
        if (legacy == null)
            return;
        if (!await db.RunnerKeys.AnyAsync(r => r.KeyHash == legacy.Value))
            db.RunnerKeys.Add(new RunnerKey { Name = "PC helper", KeyHash = legacy.Value, CreatedAt = DateTimeOffset.UtcNow });
        db.StoredValues.Remove(legacy);
        await db.SaveChangesAsync();
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    // ---- Sockets ----

    /// <summary>A helper's control socket: kept until it closes. The same helper connecting again replaces itself.</summary>
    public async Task RunControlAsync(WebSocket ws, RunnerKey key, string? machine, string? browser, CancellationToken ct, bool canShow = false,
        bool canUpdate = false, string? version = null, bool signingIn = false)
    {
        if (_live.TryRemove(key.Id, out var old))
            await CloseAsync(old.Control, "The same helper connected again.");
        _prefs[key.Id] = PrefsOf(key);
        var live = new Live(key.Id, key.Name, ws, machine, browser, canShow, canUpdate, version) { SigningIn = signingIn };
        _live[key.Id] = live;
        Changed?.Invoke(key.Id);
        await SeenAsync(key.Id, machine, browser);
        await notifier.LogAsync(EventLevel.Good,
            $"{key.Name} connected ({machine ?? "a computer"}, {browser ?? "Chrome"}{(version != null ? $", helper {version}" : "")}).");
        var buffer = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                live.LastSeen = DateTimeOffset.UtcNow;
                if (r.MessageType == WebSocketMessageType.Text && r.EndOfMessage)
                    try
                    {
                        using var msg = JsonDocument.Parse(buffer.AsMemory(0, r.Count));
                        if (msg.RootElement.TryGetProperty("signin", out var si) && si.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            live.SigningIn = si.GetBoolean();
                    }
                    catch (JsonException)
                    {
                    }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            if (_live.TryGetValue(key.Id, out var current) && ReferenceEquals(current, live))
            {
                _live.TryRemove(key.Id, out _);
                Changed?.Invoke(key.Id);
                await SeenAsync(key.Id, machine, browser);
                // Only if it stays away: a helper restarting Chrome (to show or hide its window) is back within seconds.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(90));
                    if (IsConnected(key.Id))
                        return;
                    var left = ConnectedIds.Count;
                    await notifier.NotifyAsync(NotifyKind.HelperDisconnected, EventLevel.Warning,
                        $"{live.Name} disconnected. {(left > 0 ? $"{left} other helper{(left == 1 ? " is" : "s are")} still connected." : "Pages are checked from the server until a helper is back.")}");
                });
            }
        }
    }

    private async Task SeenAsync(int id, string? machine, string? browser)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RunnerKeys.Where(r => r.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(r => r.LastSeenAt, DateTimeOffset.UtcNow)
                .SetProperty(r => r.Machine, machine)
                .SetProperty(r => r.Browser, browser));
        }
        catch (Exception e)
        {
            log.LogWarning("Couldn't note helper {Id} was seen: {Error}", id, e.Message);
        }
    }

    private static async Task CloseAsync(WebSocket ws, string why)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, why, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Playwright connected to the loopback endpoint for one helper: get a pipe from it and join the two.</summary>
    /// <summary>
    /// Asks the helper to show this page in a Chrome window, in front.
    /// False when it isn't connected or is too old to (it needs updating).
    /// </summary>
    public bool CanShow(int runnerId) => _live.TryGetValue(runnerId, out var live) && live.CanShow;

    public async Task<bool> ShowAsync(int runnerId, string url) =>
        _live.TryGetValue(runnerId, out var live) && live.CanShow && (live.SigningIn = true) && await SendAsync(live, new { show = url });

    /// <summary>Asks the helper to install a new version now, if there is one. False: not connected, or too old to.</summary>
    public async Task<bool> UpdateAsync(int runnerId) =>
        _live.TryGetValue(runnerId, out var live) && live.CanUpdate && await SendAsync(live, new { update = true });

    private static async Task<bool> SendAsync(Live live, object message)
    {
        if (live.Control.State != WebSocketState.Open)
            return false;
        await live.Send.WaitAsync();
        try
        {
            await live.Control.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            live.Send.Release();
        }
    }

    public async Task RunCdpAsync(int runnerId, WebSocket playwright, CancellationToken ct)
    {
        if (!_live.TryGetValue(runnerId, out var live) || live.Control.State != WebSocketState.Open)
            throw new InvalidOperationException("That PC helper isn't connected.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var waiting = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitingPipes[id] = waiting;
        try
        {
            await live.Send.WaitAsync(ct);
            try
            {
                await live.Control.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { open = id }), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                live.Send.Release();
            }
            var pipe = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            await JoinAsync(playwright, pipe, ct);
        }
        finally
        {
            _waitingPipes.TryRemove(id, out _);
        }
    }

    /// <summary>A helper's side of a pipe arrived: hand it to whoever asked for it, and keep it open until it's done.</summary>
    public async Task RunPipeAsync(string id, WebSocket pipe, CancellationToken ct)
    {
        if (!_waitingPipes.TryGetValue(id, out var waiting))
        {
            await pipe.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Nobody asked for this pipe.", ct);
            return;
        }
        var done = new TaskCompletionSource();
        _pipeDone[pipe] = done;
        waiting.TrySetResult(pipe);
        await done.Task.WaitAsync(ct);
    }

    private async Task JoinAsync(WebSocket a, WebSocket b, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Task.WhenAny(Copy(a, b, cts.Token), Copy(b, a, cts.Token));
        }
        finally
        {
            cts.Cancel();
            foreach (var ws in new[] { a, b })
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch (Exception)
                {
                }
            if (_pipeDone.TryRemove(b, out var done))
                done.TrySetResult();
        }
    }

    /// <summary>Copies messages one way, frame by frame (DevTools messages can be many megabytes: screenshots).</summary>
    private async Task Copy(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await from.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                    return;
                await to.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ct);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            log.LogDebug("PC browser pipe closed: {Error}", e.Message);
        }
    }
}

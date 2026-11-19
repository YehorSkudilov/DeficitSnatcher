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
/// The PC helper (DeficitSnatcher.Runner) on the owner's computer: it connects out to the site and lends it its Chrome.
///
/// The helper keeps a control WebSocket open (/api/runner/connect). When the server wants the PC's browser, Playwright
/// connects to a loopback-only endpoint here (/internal/cdp/{secret}); the server asks the helper over the control socket
/// to open a pipe (/api/runner/pipe/{id}), and the helper joins that pipe to its Chrome's DevTools socket. From then on
/// Playwright drives the PC's Chrome as if it were local. Nothing on the PC listens to the network.
/// </summary>
public sealed class RunnerHub(IServiceScopeFactory scopes, Notifier notifier, ILogger<RunnerHub> log)
{
    private const string KeyName = "runner-key-hash";

    /// <summary>The loopback endpoint's secret path part, new each start: only this process knows it.</summary>
    public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private WebSocket? _control;
    private readonly SemaphoreSlim _controlSend = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocket>> _waitingPipes = new();

    public sealed record Info(bool Connected, string? Machine, string? Browser, DateTimeOffset? Since, DateTimeOffset? LastSeen);

    private Info _info = new(false, null, null, null, null);
    public Info Status => _info with { Connected = _control is { State: WebSocketState.Open } };
    public bool Connected => _control is { State: WebSocketState.Open };

    /// <summary>Raised when the helper connects or goes away (the browser pool drops its connection).</summary>
    public event Action? Changed;

    // ---- The helper's key ----

    /// <summary>A new key for the helper (shown once); the previous one stops working.</summary>
    public async Task<string> NewKeyAsync()
    {
        var key = "dsr_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StoredValues.FirstOrDefaultAsync(v => v.Key == KeyName);
        var hash = Hash(key);
        if (row == null)
            db.StoredValues.Add(new StoredValue { Key = KeyName, Value = hash });
        else
            row.Value = hash;
        await db.SaveChangesAsync();
        if (_control != null)
            await CloseControlAsync("A new key was made.");
        return key;
    }

    public async Task<bool> HasKeyAsync()
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().StoredValues.AnyAsync(v => v.Key == KeyName);
    }

    public async Task<bool> CheckKeyAsync(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        using var scope = scopes.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().StoredValues.AsNoTracking().FirstOrDefaultAsync(v => v.Key == KeyName);
        return row != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(row.Value), Encoding.UTF8.GetBytes(Hash(key)));
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    // ---- Sockets ----

    /// <summary>The helper's control socket: kept until it closes. A newer helper replaces an older one.</summary>
    public async Task RunControlAsync(WebSocket ws, string? machine, string? browser, CancellationToken ct)
    {
        if (_control != null)
            await CloseControlAsync("Another PC helper connected.");
        _control = ws;
        _info = new Info(true, machine, browser, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Changed?.Invoke();
        await notifier.LogAsync(EventLevel.Good, $"PC helper connected ({machine ?? "a computer"}, {browser ?? "Chrome"}).");
        var buffer = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                _info = _info with { LastSeen = DateTimeOffset.UtcNow };
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_control, ws))
            {
                _control = null;
                _info = _info with { Connected = false, LastSeen = DateTimeOffset.UtcNow };
                Changed?.Invoke();
                await notifier.NotifyAsync(NotifyKind.Error, EventLevel.Warning,
                    "PC helper disconnected: pages set to run on your PC can't be checked until it's back.");
            }
        }
    }

    private async Task CloseControlAsync(string why)
    {
        var old = _control;
        _control = null;
        try
        {
            if (old is { State: WebSocketState.Open })
                await old.CloseAsync(WebSocketCloseStatus.PolicyViolation, why, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Playwright connected to the loopback endpoint: get a pipe from the helper and join the two.</summary>
    public async Task RunCdpAsync(WebSocket playwright, CancellationToken ct)
    {
        var control = _control ?? throw new InvalidOperationException("The PC helper isn't connected.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var waiting = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitingPipes[id] = waiting;
        try
        {
            await _controlSend.WaitAsync(ct);
            try
            {
                await control.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { open = id }), WebSocketMessageType.Text, true, ct);
                log.LogInformation("Asked the PC helper for a browser pipe {Id} (socket {State})", id, control.State);
            }
            finally
            {
                _controlSend.Release();
            }
            var pipe = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            await JoinAsync(playwright, pipe, ct);
        }
        finally
        {
            _waitingPipes.TryRemove(id, out _);
        }
    }

    /// <summary>The helper's side of a pipe arrived: hand it to whoever asked for it, and keep it open until it's done.</summary>
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

    private readonly ConcurrentDictionary<WebSocket, TaskCompletionSource> _pipeDone = new();

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

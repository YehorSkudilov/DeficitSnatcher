using System.Net;
using DeficitSnatcher.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Pc;

public sealed record RunnerStatusDto(bool HasKey, bool Connected, string? Machine, string? Browser, DateTimeOffset? Since, DateTimeOffset? LastSeen,
    int ConnectedCount);
public sealed record NewHelperRequest(string? Name);
public sealed record NewHelperResponse(int Id, string Key);
public sealed record RenameRequest(string Name);
public sealed record OpenLoginRequest(string Domain);
public sealed record ChecksRequest(bool On, string? Domain);
public sealed record HelperVersionsDto(string? Latest, string? Error);
public sealed record WorkerDto(string Worker, int? Id, string Name, bool Connected, string? Machine, string? Browser, DateTimeOffset? Since,
    DateTimeOffset? LastSeen, DateTimeOffset? CreatedAt, string? Version, bool CanUpdate, bool ChecksEnabled, List<string> DisabledDomains, int ChecksLastHour, int ChecksToday, int PagesLastHour, DateTimeOffset? LastCheck,
    int ErrorsLastHour, List<StoreLoginDto> Logins);

/// <summary>The PC helpers' endpoints, and the site's view of them (the Helpers tab).</summary>
public static class RunnerEndpoints
{
    public static void Map(WebApplication app)
    {
        var hub = app.Services.GetRequiredService<RunnerHub>();

        // A helper's sockets: authenticated with its key (Authorization: Runner <key>), not the owner's sign-in.
        app.Map("/api/runner/connect", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest("WebSocket only.");
            if (await hub.FindAsync(Key(http)) is not { } key)
                return Results.Unauthorized();
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            var can = http.Request.Query["can"].ToString().Split(',');
            await hub.RunControlAsync(ws, key, http.Request.Query["machine"], http.Request.Query["browser"], http.RequestAborted,
                canShow: can.Contains("show"), canUpdate: can.Contains("update"), version: http.Request.Query["version"].ToString() is { Length: > 0 and < 40 } v ? v : null,
                signingIn: http.Request.Query["signin"] == "1");
            return Results.Empty;
        }).AllowAnonymous().DisableRateLimiting();

        app.Map("/api/runner/pipe/{id}", async (HttpContext http, string id) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest("WebSocket only.");
            if (await hub.FindAsync(Key(http)) == null)
                return Results.Unauthorized();
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            await hub.RunPipeAsync(id, ws, http.RequestAborted);
            return Results.Empty;
        }).AllowAnonymous().DisableRateLimiting();

        // Where Playwright connects to reach a helper's Chrome: this machine only, with a path only this process knows.
        app.Map("/internal/cdp/{secret}/{runnerId:int}", async (HttpContext http, string secret, int runnerId) =>
        {
            if (secret != hub.Secret || http.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
                || http.Request.Headers.ContainsKey("X-Forwarded-For"))
                return Results.NotFound();
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest("WebSocket only.");
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            await hub.RunCdpAsync(runnerId, ws, http.RequestAborted);
            return Results.Empty;
        }).AllowAnonymous().DisableRateLimiting();

        // The site's side (signed in).
        app.MapGet("/api/runner/status", async () =>
        {
            var all = await hub.ListAsync();
            var first = all.FirstOrDefault(r => r.Connected);
            return new RunnerStatusDto(all.Count > 0, first != null, first?.Machine, first?.Browser, first?.Since, first?.LastSeen,
                all.Count(r => r.Connected));
        });
        // Every helper and the server, with what each has done lately and (helpers) which stores they're signed in to.
        app.MapGet("/api/runners", async (AppDbContext db, LoginWatcher logins) =>
        {
            var all = await hub.ListAsync();
            var now = DateTimeOffset.UtcNow;
            var hourAgo = now.AddHours(-1);
            var dayAgo = now.AddHours(-24);
            var stats = await db.Scans.AsNoTracking().Where(x => x.At >= dayAgo)
                .GroupBy(x => x.Worker ?? WorkerPool.Server)
                .Select(g => new
                {
                    Worker = g.Key,
                    Day = g.Count(),
                    Hour = g.Count(x => x.At >= hourAgo),
                    Pages = g.Where(x => x.At >= hourAgo).Select(x => x.SiteId).Distinct().Count(),
                    Errors = g.Count(x => x.At >= hourAgo && x.Outcome == ScanOutcome.Error),
                    Last = g.Max(x => (DateTimeOffset?)x.At),
                })
                .ToDictionaryAsync(x => x.Worker);
            WorkerDto Row(string worker, int? id, string name, bool connected, string? machine, string? browser, DateTimeOffset? since,
                DateTimeOffset? lastSeen, DateTimeOffset? created, string? version, bool canUpdate, bool enabled, List<string> off,
                List<StoreLoginDto> l)
            {
                stats.TryGetValue(worker, out var st);
                return new WorkerDto(worker, id, name, connected, machine, browser, since, lastSeen, created, version, canUpdate, enabled, off, st?.Hour ?? 0, st?.Day ?? 0,
                    st?.Pages ?? 0, st?.Last, st?.Errors ?? 0, l);
            }
            var rows = new List<WorkerDto>();
            foreach (var r in all)
                rows.Add(Row(WorkerPool.Pc(r.Id), r.Id, r.Name, r.Connected, r.Machine, r.Browser, r.Since, r.LastSeen, r.CreatedAt,
                    r.Version, r.CanUpdate, r.ChecksEnabled, r.DisabledDomains, await logins.ListAsync(r.Id)));
            rows.Add(Row(WorkerPool.Server, null, "Server", true, Environment.MachineName, "Chromium (headless)", null, null, null, null, false, true, [], []));
            return rows;
        });
        app.MapPut("/api/runners/{id:int}/checks", async (int id, ChecksRequest request) =>
            await hub.SetChecksAsync(id, request.On, request.Domain) ? Results.NoContent() : Results.NotFound());
        app.MapGet("/api/runners/latest-version", async (HelperVersions versions) =>
        {
            var (latest, error) = await versions.LatestAsync();
            return new HelperVersionsDto(latest, error);
        });
        app.MapPost("/api/runners/{id:int}/update", async (int id) =>
        {
            if (!hub.IsConnected(id))
                return Results.Conflict(new { error = $"{hub.NameOf(id)} isn't connected." });
            return await hub.UpdateAsync(id)
                ? Results.Accepted()
                : Results.Conflict(new { error = $"{hub.NameOf(id)} is too old to update itself. Download it again from this tab and open it once (your setup is kept); after that it updates by itself." });
        });
        app.MapPost("/api/runners/{id:int}/logins/check", (int id, LoginWatcher logins) =>
        {
            if (!hub.IsConnected(id))
                return Results.Conflict(new { error = $"{hub.NameOf(id)} isn't connected." });
            logins.CheckSoon(id);
            return Results.Accepted();
        });
        app.MapPost("/api/runners/{id:int}/login", async (int id, OpenLoginRequest request, LoginWatcher logins, CancellationToken ct) =>
        {
            if (!hub.IsConnected(id))
                return Results.Conflict(new { error = $"{hub.NameOf(id)} isn't connected. Start it on that computer first." });
            try
            {
                await logins.OpenAsync(id, request.Domain.Trim().ToLowerInvariant(), ct);
                return Results.NoContent();
            }
            catch (Tracking.PcHelperException e)
            {
                return Results.Conflict(new { error = e.Message });
            }
        });
        app.MapPost("/api/runner/key", async (NewHelperRequest? request) =>
        {
            var (id, key) = await hub.NewKeyAsync(request?.Name);
            return new NewHelperResponse(id, key);
        });
        app.MapPut("/api/runners/{id:int}", async (int id, RenameRequest request) =>
            await hub.RenameAsync(id, request.Name) ? Results.NoContent() : Results.NotFound());
        app.MapDelete("/api/runners/{id:int}", async (int id) => await hub.RevokeAsync(id) ? Results.NoContent() : Results.NotFound());
    }

    private static string? Key(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Runner ", StringComparison.Ordinal) ? header["Runner ".Length..].Trim() : null;
    }
}

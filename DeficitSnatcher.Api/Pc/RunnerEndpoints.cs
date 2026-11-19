using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace DeficitSnatcher.Api.Pc;

public sealed record RunnerStatusDto(bool HasKey, bool Connected, string? Machine, string? Browser, DateTimeOffset? Since, DateTimeOffset? LastSeen);
public sealed record RunnerKeyDto(string Key);

/// <summary>The PC helper's endpoints, and the site's view of it (Settings → PC helper).</summary>
public static class RunnerEndpoints
{
    public static void Map(WebApplication app)
    {
        var hub = app.Services.GetRequiredService<RunnerHub>();

        // The helper's sockets: authenticated with its key (Authorization: Runner <key>), not the owner's sign-in.
        app.Map("/api/runner/connect", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest("WebSocket only.");
            if (!await hub.CheckKeyAsync(Key(http)))
                return Results.Unauthorized();
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            await hub.RunControlAsync(ws, http.Request.Query["machine"], http.Request.Query["browser"], http.RequestAborted);
            return Results.Empty;
        }).AllowAnonymous().DisableRateLimiting();

        app.Map("/api/runner/pipe/{id}", async (HttpContext http, string id) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest("WebSocket only.");
            if (!await hub.CheckKeyAsync(Key(http)))
                return Results.Unauthorized();
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            await hub.RunPipeAsync(id, ws, http.RequestAborted);
            return Results.Empty;
        }).AllowAnonymous().DisableRateLimiting();

        // Where Playwright connects to reach the PC's Chrome: this machine only, with a path only this process knows.
        app.Map("/internal/cdp/{secret}", async (HttpContext http, string secret) =>
        {
            if (secret != hub.Secret || http.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
                || http.Request.Headers.ContainsKey("X-Forwarded-For"))
                return Results.NotFound();
            if (!http.WebSockets.IsWebSocketRequest)
                // Playwright first asks /json/version-style questions only for http:// endpoints; ws:// goes straight here.
                return Results.BadRequest("WebSocket only.");
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            await hub.RunCdpAsync(ws, http.RequestAborted);
            return Results.Empty;
        }).AllowAnonymous().DisableRateLimiting();

        // The site's side (signed in).
        app.MapGet("/api/runner/status", async () =>
        {
            var s = hub.Status;
            return new RunnerStatusDto(await hub.HasKeyAsync(), s.Connected, s.Machine, s.Browser, s.Since, s.LastSeen);
        });
        app.MapPost("/api/runner/key", async () => new RunnerKeyDto(await hub.NewKeyAsync()));
    }

    private static string? Key(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Runner ", StringComparison.Ordinal) ? header["Runner ".Length..].Trim() : null;
    }
}

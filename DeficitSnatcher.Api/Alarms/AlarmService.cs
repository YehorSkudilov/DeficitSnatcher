using DeficitSnatcher.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Alarms;

public sealed record AlarmDto(long Id, DateTimeOffset At, int? SiteId, string Product, string Store, decimal? Price, string Url, string Message, bool Test);

/// <summary>
/// Rings the Android app: every match (in stock, in your range) is kept as an alarm, and the app's long poll
/// (GET /api/alarms?after=…) gets it the moment it's raised.
/// </summary>
public sealed class AlarmService(IServiceScopeFactory scopes, ILogger<AlarmService> log)
{
    /// <summary>Completed (and replaced) each time an alarm is raised: what waiting long polls wake on.</summary>
    private TaskCompletionSource _raised = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    public async Task<AlarmDto?> RaiseAsync(string product, string store, decimal? price, string url, string message, int? siteId, bool test = false)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alarm = new Alarm
            {
                At = DateTimeOffset.UtcNow, SiteId = siteId, Product = Cut(product, 200), Store = Cut(store, 200), Price = price,
                Url = Cut(url, 2000), Message = Cut(message, 1000), Test = test,
            };
            db.Alarms.Add(alarm);
            await db.SaveChangesAsync();
            TaskCompletionSource raised;
            lock (_gate)
            {
                raised = _raised;
                _raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            raised.TrySetResult();
            return ToDto(alarm);
        }
        catch (Exception e)
        {
            log.LogError(e, "Couldn't raise the alarm: {Message}", message);
            return null;
        }
    }

    /// <summary>Alarms after this one (oldest first); none yet: waits up to <paramref name="wait"/> for one.</summary>
    public async Task<List<AlarmDto>> AfterAsync(long after, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            Task raised;
            lock (_gate)
                raised = _raised.Task;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var list = await db.Alarms.AsNoTracking().Where(a => a.Id > after).OrderBy(a => a.Id).Take(20).ToListAsync(ct);
                if (list.Count > 0)
                    return list.Select(ToDto).ToList();
            }
            var left = until - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
                return [];
            try
            {
                await raised.WaitAsync(left, ct);
            }
            catch (TimeoutException)
            {
                return [];
            }
        }
    }

    /// <summary>The newest alarm's id (0: none): where a newly started app starts listening from.</summary>
    public async Task<long> LatestIdAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Alarms.MaxAsync(a => (long?)a.Id, ct) ?? 0;
    }

    public async Task<List<AlarmDto>> RecentAsync(int count, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Alarms.AsNoTracking().OrderByDescending(a => a.Id).Take(count).ToListAsync(ct)).Select(ToDto).ToList();
    }

    private static AlarmDto ToDto(Alarm a) => new(a.Id, a.At, a.SiteId, a.Product, a.Store, a.Price, a.Url, a.Message, a.Test);

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
}

/// <summary>The Android app's endpoints (signed in like the site).</summary>
public static class AlarmEndpoints
{
    public static void Map(WebApplication app)
    {
        // Long poll: alarms after ?after=id, waiting up to ?wait= seconds (at most 45) when there are none yet.
        app.MapGet("/api/alarms", async (long? after, int? wait, AlarmService alarms, CancellationToken ct) =>
        {
            var from = after ?? await alarms.LatestIdAsync(ct);
            var list = await alarms.AfterAsync(from, TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 45)), ct);
            return new { latest = list.Count > 0 ? list[^1].Id : from, alarms = list };
        });
        app.MapGet("/api/alarms/recent", (AlarmService alarms, CancellationToken ct) => alarms.RecentAsync(20, ct));
        // Rings the app once, to check it's set up.
        app.MapPost("/api/alarms/test", async (AlarmService alarms, IConfiguration config) =>
        {
            var site = (config["App:PublicUrl"] ?? "").Trim().TrimEnd('/');
            var alarm = await alarms.RaiseAsync("Test alarm", "Deficit Snatcher", null, site.Length > 0 ? site : "https://example.com",
                "Test: the app rings like this when something is in stock in your range.", null, test: true);
            return alarm == null ? Results.Problem("Couldn't raise it.") : Results.Ok(alarm);
        });
    }
}

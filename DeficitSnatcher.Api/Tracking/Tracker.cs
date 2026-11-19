using System.Collections.Concurrent;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Checks every enabled site on its interval (with jitter, so checks don't land on a fixed beat), a few at a time,
/// never two of the same host closer than the settings allow, and backing off a site that keeps failing.
/// </summary>
public sealed class Tracker(IServiceScopeFactory scopes, SettingsStore settingsStore, CheckService checks, ILogger<Tracker> log)
    : BackgroundService
{
    private readonly ConcurrentDictionary<int, DateTimeOffset> _nextDue = new();
    private readonly ConcurrentDictionary<int, byte> _running = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hostLast = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    /// <summary>When each site is checked next (the dashboard shows it).</summary>
    public DateTimeOffset? NextCheck(int siteId) => _nextDue.TryGetValue(siteId, out var at) ? at : null;

    public bool IsChecking(int siteId) => _running.ContainsKey(siteId);

    /// <summary>Check it as soon as possible (after a site is added or edited).</summary>
    public void CheckSoon(int siteId) => _nextDue[siteId] = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Tracker tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (DateTimeOffset.UtcNow - _lastCleanup > TimeSpan.FromHours(1))
        {
            _lastCleanup = DateTimeOffset.UtcNow;
            if (settings.HistoryDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.HistoryDays);
                await db.PricePoints.Where(p => p.At < cutoff).ExecuteDeleteAsync(ct);
                await db.Events.Where(e => e.At < cutoff).ExecuteDeleteAsync(ct);
            }
        }

        if (!settings.TrackingEnabled)
            return;

        var sites = await db.Sites.AsNoTracking()
            .Where(s => s.Enabled && s.Product!.Enabled)
            .Select(s => new { s.Id, s.Url, s.IntervalSeconds, s.LastCheckedAt, s.ConsecutiveErrors })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var site in sites.OrderBy(s => _nextDue.GetValueOrDefault(s.Id, s.LastCheckedAt ?? DateTimeOffset.MinValue)))
        {
            if (_running.Count >= settings.MaxConcurrentChecks)
                break;
            if (_running.ContainsKey(site.Id))
                continue;
            var interval = site.IntervalSeconds ?? settings.DefaultIntervalSeconds;
            var due = _nextDue.GetOrAdd(site.Id, _ => site.LastCheckedAt?.AddSeconds(interval) ?? now);
            if (due > now)
                continue;
            var host = Uri.TryCreate(site.Url, UriKind.Absolute, out var uri) ? uri.Host : site.Url;
            if (_hostLast.TryGetValue(host, out var last) && now - last < TimeSpan.FromSeconds(settings.MinSecondsPerHost))
                continue;

            _hostLast[host] = now;
            _running[site.Id] = 0;
            var id = site.Id;
            _ = Task.Run(async () =>
            {
                var errors = 0;
                try
                {
                    var outcome = await checks.CheckAsync(id, ct);
                    errors = outcome.Error == null ? 0 : site.ConsecutiveErrors + 1;
                }
                catch (Exception e)
                {
                    log.LogError(e, "Check of site {Id} crashed", id);
                    errors = site.ConsecutiveErrors + 1;
                }
                finally
                {
                    _nextDue[id] = DateTimeOffset.UtcNow + NextWait(interval, settings.JitterPercent, errors);
                    _running.TryRemove(id, out _);
                }
            }, CancellationToken.None);
        }

        // Forget sites that were deleted or turned off.
        var ids = sites.Select(s => s.Id).ToHashSet();
        foreach (var id in _nextDue.Keys.Where(k => !ids.Contains(k)).ToList())
            _nextDue.TryRemove(id, out _);
    }

    /// <summary>The interval with ± jitter, and longer while a site keeps failing (up to 5×, at most 30 minutes).</summary>
    private static TimeSpan NextWait(int intervalSeconds, int jitterPercent, int errors)
    {
        var seconds = (double)intervalSeconds;
        if (errors > 2)
            seconds = Math.Min(seconds * Math.Min(errors - 1, 5), Math.Max(intervalSeconds, 1800));
        var jitter = seconds * jitterPercent / 100.0;
        seconds += (Random.Shared.NextDouble() * 2 - 1) * jitter;
        return TimeSpan.FromSeconds(Math.Max(3, seconds));
    }
}

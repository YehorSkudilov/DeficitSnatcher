using System.Collections.Concurrent;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Checks every enabled site on its interval (with jitter, so checks don't land on a fixed beat), a few at a time,
/// never two of the same host closer than the settings allow, and backing off a site that keeps failing.
/// </summary>
public sealed class Tracker(IServiceScopeFactory scopes, SettingsStore settingsStore, CheckService checks, Ai.RestockForecaster forecaster,
    ILogger<Tracker> log)
    : BackgroundService
{
    /// <summary>The most one check may take in all (page, reading it, saving): past it, the check is given up.</summary>
    private static TimeSpan CheckLimit(Settings.BotSettings settings) => CheckService.FetchLimit(settings) + TimeSpan.FromSeconds(60);

    private DateTimeOffset _lastForecastLookup;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTimeOffset? Next, DateTimeOffset At)> _forecasts = new();

    /// <summary>A restock looks likely now for this page: from 30 minutes before its forecast to 90 after (forecasts are
    /// looked at every 10 minutes).</summary>
    private async Task<bool> RestockLikelyAsync(int siteId, DateTimeOffset now, CancellationToken ct)
    {
        if (!_forecasts.TryGetValue(siteId, out var f) || now - f.At > TimeSpan.FromMinutes(10))
        {
            try
            {
                f = ((await forecaster.ForecastAsync(siteId, ct))?.NextAt, now);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                f = (null, now);
            }
            _forecasts[siteId] = f;
        }
        return f.Next is { } next && now >= next.AddMinutes(-30) && now <= next.AddMinutes(90);
    }

    /// <summary>Is this page in its likely-restock window (checked every minute)?</summary>
    public bool RestockWindow(int siteId) =>
        _forecasts.TryGetValue(siteId, out var f) && f.Next is { } next && DateTimeOffset.UtcNow >= next.AddMinutes(-30)
        && DateTimeOffset.UtcNow <= next.AddMinutes(90);

    private readonly ConcurrentDictionary<int, DateTimeOffset> _nextDue = new();
    private readonly ConcurrentDictionary<int, byte> _running = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hostLast = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _hostRunning = new(StringComparer.OrdinalIgnoreCase);
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

        // Every few minutes: one store's restock pattern looked up online, when it's never been, is a week old, or its
        // predicted restock has passed (so the forecast moves on). One at a time keeps the AI's use low.
        if (DateTimeOffset.UtcNow - _lastForecastLookup > TimeSpan.FromMinutes(3))
        {
            _lastForecastLookup = DateTimeOffset.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    await forecaster.RefreshOneStaleAsync(CancellationToken.None);
                }
                catch (Exception e)
                {
                    log.LogWarning("Restock lookup failed: {Error}", e.Message);
                }
            });
        }
        if (DateTimeOffset.UtcNow - _lastCleanup > TimeSpan.FromHours(1))
        {
            _lastCleanup = DateTimeOffset.UtcNow;
            if (settings.HistoryDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.HistoryDays);
                await db.PricePoints.Where(p => p.At < cutoff).ExecuteDeleteAsync(ct);
                await db.Events.Where(e => e.At < cutoff).ExecuteDeleteAsync(ct);
            }
            if (settings.ScanHistoryDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.ScanHistoryDays);
                await db.Scans.Where(x => x.At < cutoff).ExecuteDeleteAsync(ct);
            }
            if (settings.CheckoutHistoryDays > 0)
            {
                // Screenshots go with them (cascade). Bought orders are kept: they count against the limits.
                var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.CheckoutHistoryDays);
                await db.Purchases.Where(p => p.StartedAt < cutoff && !Buying.Buyer.CountsAsBought.Contains(p.Status))
                    .ExecuteDeleteAsync(ct);
            }
        }

        if (!settings.TrackingEnabled)
        {
            if (settings.TrackingResumeAt is { } resumeAt && resumeAt <= DateTimeOffset.UtcNow)
            {
                settings = await settingsStore.UpdateAsync(s => s with { TrackingEnabled = true, TrackingResumeAt = null }, ct);
                log.LogInformation("Scheduled checking resumed after its cooldown");
            }
            else
                return;
        }

        var sites = await db.Sites.AsNoTracking()
            .Where(s => s.Enabled && s.Product!.Enabled)
            .Select(s => new { s.Id, s.Url, s.IntervalSeconds, s.LastCheckedAt, s.ConsecutiveErrors })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var site in sites.OrderBy(s => _nextDue.GetValueOrDefault(s.Id, s.LastCheckedAt ?? DateTimeOffset.MinValue)))
        {
            if (_running.ContainsKey(site.Id))
                continue;
            var interval = site.IntervalSeconds ?? settings.DefaultIntervalSeconds;
            // Around a likely restock (its forecast): every minute, so a drop isn't missed; the rest of the time, the usual pace.
            if (await RestockLikelyAsync(site.Id, now, ct))
                interval = Math.Min(interval, 60);
            if (settings.StoreIntervals.FirstOrDefault(x => x.Matches(site.Url)) is { } strict)
                interval = Math.Max(interval, strict.Minutes * 60);
            // Keep scheduled polling at or below one request per minute per page, even for older saved settings.
            interval = Math.Max(interval, 60);
            var due = _nextDue.GetOrAdd(site.Id, _ => site.LastCheckedAt?.AddSeconds(interval) ?? now);
            if (due > now)
                continue;
            // All slots busy: it's overdue, and goes first when one frees up (the loop runs every few seconds).
            if (_running.Count >= settings.MaxConcurrentChecks)
                continue;
            var host = Uri.TryCreate(site.Url, UriKind.Absolute, out var uri) ? uri.IdnHost : site.Url;
            if (_hostRunning.ContainsKey(host))
                continue;
            if (_hostLast.TryGetValue(host, out var last) && now - last < TimeSpan.FromSeconds(settings.MinSecondsPerHost))
                continue;

            _hostLast[host] = now;
            _hostRunning[host] = 0;
            _running[site.Id] = 0;
            var id = site.Id;
            _ = Task.Run(async () =>
            {
                var errors = 0;
                // A hard limit per check: whatever hangs (browser, database), the slot comes back and other pages get checked.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(CheckLimit(settings));
                try
                {
                    var outcome = await checks.CheckAsync(id, limit.Token).WaitAsync(CheckLimit(settings) + TimeSpan.FromSeconds(15), ct);
                    errors = outcome.Error == null ? 0 : site.ConsecutiveErrors + 1;
                }
                catch (Exception e) when (!ct.IsCancellationRequested && e is TimeoutException or OperationCanceledException)
                {
                    log.LogWarning("Check of site {Id} took over {Limit} s; given up, next one as usual", id, CheckLimit(settings).TotalSeconds);
                    errors = site.ConsecutiveErrors + 1;
                }
                catch (Exception e)
                {
                    log.LogError(e, "Check of site {Id} crashed", id);
                    errors = site.ConsecutiveErrors + 1;
                }
                finally
                {
                    _nextDue[id] = DateTimeOffset.UtcNow + NextWait(interval, settings.JitterPercent, settings.SlowDownFailingPages ? errors : 0);
                    _running.TryRemove(id, out _);
                    _hostRunning.TryRemove(host, out _);
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

using System.Collections.Concurrent;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// A store that blocked a page's check isn't asked again for a while: 15 minutes, doubling with each block in a row (up to
/// 6 hours, a little random), until a check goes through. Checking again every minute keeps a flag on; resting lets it wear
/// off.
/// </summary>
public sealed class Backoff
{
    private readonly ConcurrentDictionary<int, (int Strikes, DateTimeOffset Until)> _sites = new();
    // Learned pacing: a store that lets one check through and blocks the next a minute or two later is rate-limiting. The
    // page's checks are then spaced at least this far apart (doubled from the gap that got blocked, 2–30 min), easing off
    // a little after every few checks that go through.
    private readonly ConcurrentDictionary<int, (DateTimeOffset LastOk, double GapSeconds, int Streak)> _pace = new();

    /// <summary>The store blocked this page's check: how long it rests.</summary>
    public TimeSpan Blocked(int siteId, TimeSpan? retryAfter = null)
    {
        var strikes = _sites.TryGetValue(siteId, out var b) ? b.Strikes + 1 : 1;
        var minutes = Math.Min(15 * Math.Pow(2, strikes - 1), 360) * (0.85 + Random.Shared.NextDouble() * 0.3);
        var rest = TimeSpan.FromMinutes(minutes);
        // Honor the store's explicit cooldown when it asks us to wait longer than our local backoff.
        if (retryAfter is { } requested && requested > rest)
            rest = requested > TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : requested;
        _sites[siteId] = (strikes, DateTimeOffset.UtcNow + rest);
        if (_pace.TryGetValue(siteId, out var p) && DateTimeOffset.UtcNow - p.LastOk < TimeSpan.FromMinutes(45))
        {
            var gap = Math.Clamp(Math.Max(p.GapSeconds, (DateTimeOffset.UtcNow - p.LastOk).TotalSeconds * 2), 120, 1800);
            _pace[siteId] = (p.LastOk, gap, 0);
        }
        return rest;
    }

    /// <summary>A check went through: no more resting.</summary>
    public void Succeeded(int siteId)
    {
        _sites.TryRemove(siteId, out _);
        _pace.AddOrUpdate(siteId, _ => (DateTimeOffset.UtcNow, 0, 1), (_, p) =>
        {
            var streak = p.Streak + 1;
            var gap = p.GapSeconds;
            if (gap > 0 && streak % 6 == 0)
                gap = gap * 0.85 < 90 ? 0 : gap * 0.85;
            return (DateTimeOffset.UtcNow, gap, streak);
        });
    }

    /// <summary>The least time between this page's checks that the store has shown it tolerates, or null.</summary>
    public TimeSpan? MinGap(int siteId) =>
        _pace.TryGetValue(siteId, out var p) && p.GapSeconds > 0 ? TimeSpan.FromSeconds(p.GapSeconds) : null;

    /// <summary>Until when the page rests, or null.</summary>
    public DateTimeOffset? RestingUntil(int siteId) =>
        _sites.TryGetValue(siteId, out var b) && b.Until > DateTimeOffset.UtcNow ? b.Until : null;
}

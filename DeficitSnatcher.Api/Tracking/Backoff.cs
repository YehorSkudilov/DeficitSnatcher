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
        return rest;
    }

    /// <summary>A check went through: no more resting.</summary>
    public void Succeeded(int siteId) => _sites.TryRemove(siteId, out _);

    /// <summary>Until when the page rests, or null.</summary>
    public DateTimeOffset? RestingUntil(int siteId) =>
        _sites.TryGetValue(siteId, out var b) && b.Until > DateTimeOffset.UtcNow ? b.Until : null;
}

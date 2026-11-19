using System.Collections.Concurrent;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Tracking;
using Data = DeficitSnatcher.Api.Data;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Pc;

/// <summary>
/// On the owner's PC, a store's "are you human" check (Walmart's Press &amp; Hold) is for the owner to do: the window is
/// brought to the front, they get an alert, and the job waits until the page is the store again. Nothing here solves it.
/// </summary>
public sealed class HumanCheck(Notifier notifier)
{
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastAlert = new();

    /// <summary>Waits while the page is a bot check; throws BlockedException if it's still there after the wait.</summary>
    public async Task WaitIfBlockedAsync(IPage page, int siteId, string siteName, CancellationToken ct)
    {
        string? vendor;
        try
        {
            vendor = BlockDetector.Detect(await page.ContentAsync());
        }
        catch (PlaywrightException)
        {
            return;
        }
        if (vendor == null)
            return;

        try
        {
            await page.BringToFrontAsync();
        }
        catch (PlaywrightException)
        {
        }
        var now = DateTimeOffset.UtcNow;
        if (!_lastAlert.TryGetValue(siteId, out var last) || now - last > TimeSpan.FromMinutes(10))
        {
            _lastAlert[siteId] = now;
            await notifier.NotifyAsync(NotifyKind.InStock, Data.EventLevel.Warning,
                $"{siteName}: the store wants you to prove you're human ({vendor}). Do it in the Chrome window of the PC helper on your computer; "
                + $"the bot waits {Wait.TotalMinutes:0} minutes.", siteId);
        }

        var until = DateTimeOffset.UtcNow + Wait;
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            try
            {
                if (BlockDetector.Detect(await page.ContentAsync()) == null)
                {
                    await notifier.LogAsync(Data.EventLevel.Good, $"{siteName}: human check done, carrying on.", siteId);
                    try
                    {
                        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 15000 });
                    }
                    catch (TimeoutException)
                    {
                    }
                    return;
                }
            }
            catch (PlaywrightException)
            {
                // Navigating while we looked: look again.
            }
        }
        throw new BlockedException($"Blocked by {vendor} on your PC: the human check wasn't done within {Wait.TotalMinutes:0} minutes.");
    }
}

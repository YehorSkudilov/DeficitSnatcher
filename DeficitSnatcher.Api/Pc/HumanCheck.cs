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
public sealed class HumanCheck(Notifier notifier, RunnerHub hub, BrowserPool browsers)
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

        // The helper opens this page in a window of its own for the owner, and the bot's tabs stay out of sight. This job
        // doesn't wait: the next check carries on once it's done.
        if (browsers.RunnerOf(page.Context) is { } runnerId && hub.CanShow(runnerId))
        {
            var shown = await hub.ShowAsync(runnerId, page.Url);
            if (Throttle(siteId))
                await notifier.NotifyAsync(NotifyKind.HumanCheck, Data.EventLevel.Warning, shown
                    ? $"{siteName}: the store wants you to prove you're human ({vendor}). Chrome on {hub.NameOf(runnerId)} came up with the page: "
                      + "do it there, and the next check carries on."
                    : $"{siteName}: the store wants you to prove you're human ({vendor}), but {hub.NameOf(runnerId)} couldn't bring the page up. "
                      + "Do it in that computer's Chrome, or update the helper (download it again from the Helpers tab and open it).",
                    siteId);
            throw new BlockedException($"Blocked by {vendor} on {hub.NameOf(runnerId)}: "
                + (shown ? "Chrome came up with the page for you to prove you're human." : "it couldn't bring the page up."));
        }

        await ShowAsync(page);
        var now = DateTimeOffset.UtcNow;
        if (Throttle(siteId))
        {
            await notifier.NotifyAsync(NotifyKind.HumanCheck, Data.EventLevel.Warning,
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

    /// <summary>One alert per page per 10 minutes.</summary>
    private bool Throttle(int siteId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastAlert.TryGetValue(siteId, out var last) && now - last < TimeSpan.FromMinutes(10))
            return false;
        _lastAlert[siteId] = now;
        return true;
    }

    /// <summary>Brings the helper's Chrome window up (un-minimised) and in front, on this page's tab.</summary>
    public static async Task ShowAsync(IPage page)
    {
        try
        {
            var cdp = await page.Context.NewCDPSessionAsync(page);
            var window = await cdp.SendAsync("Browser.getWindowForTarget");
            if (window is { } w && w.TryGetProperty("windowId", out var id))
                await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
                {
                    ["windowId"] = id.GetInt32(),
                    ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" },
                });
            await cdp.DetachAsync();
        }
        catch (PlaywrightException)
        {
        }
        try
        {
            await page.BringToFrontAsync();
        }
        catch (PlaywrightException)
        {
        }
    }
}

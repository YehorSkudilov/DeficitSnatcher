using System.IO.Compression;
using System.Text;
using DeficitSnatcher.Api.Ai;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// What happens after a scan, in the background so checks don't wait for it: the scan's snapshot (the page in a browser
/// and its source), and the AI re-learning a page that stopped reading.
/// </summary>
public sealed class ScanFollowUp(IServiceScopeFactory scopes, BrowserPool browsers, SecretBox box, SettingsStore settingsStore,
    OpenAiClient ai, PageSetup setup, Notifier notifier, ILogger<ScanFollowUp> log)
{
    // A couple of browser pages at a time at most: snapshots are a nice-to-have next to the checks themselves.
    private readonly SemaphoreSlim _browserSlots = new(2, 2);
    private readonly HashSet<int> _fixing = [];

    /// <summary>Keeps the scan's snapshot: the source now, the screenshot as soon as a browser slot is free.</summary>
    public void Snapshot(long scanId, string url, string? cookiesSealed, string reason, string? source, string? sourceType, bool onPc = false)
    {
        _ = Task.Run(async () =>
        {
            byte[]? shot = null;
            string? error = null;
            await _browserSlots.WaitAsync();
            try
            {
                var settings = await settingsStore.GetAsync();
                var cookies = cookiesSealed == null ? [] : SiteCookies.Parse(box.Decrypt(cookiesSealed), url);
                await using var lease = await browsers.OpenAsync(settings, cookies, onPc, CancellationToken.None);
                var page = await lease.NewPageAsync();
                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                }
                catch (TimeoutException)
                {
                }
                shot = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 60, Timeout = 8000 });
            }
            catch (Exception e)
            {
                error = "Screenshot: " + e.Message.Split('\n')[0];
            }
            finally
            {
                _browserSlots.Release();
            }

            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (!await db.Scans.AnyAsync(s => s.Id == scanId))
                    return;
                db.ScanSnapshots.Add(new ScanSnapshot
                {
                    ScanId = scanId,
                    Reason = reason.Length > 300 ? reason[..300] : reason,
                    Screenshot = shot == null ? null : box.EncryptBytes(shot),
                    Source = source == null ? null : box.EncryptBytes(Gzip(source.Length > 5_000_000 ? source[..5_000_000] : source)),
                    SourceType = sourceType,
                    Error = error is { Length: > 1000 } ? error[..1000] : error,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception e)
            {
                log.LogWarning("Couldn't keep the snapshot of scan {Scan}: {Error}", scanId, e.Message);
            }
        });
    }

    public byte[] ReadSource(byte[] sealedGzip)
    {
        using var input = new GZipStream(new MemoryStream(box.DecryptBytes(sealedGzip)), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    /// <summary>
    /// The page kept failing or showing no price or stock: the AI looks at it again and, when what it suggests reads the
    /// page, the page's settings are changed to it. At most every 6 hours per page.
    /// </summary>
    public async Task MaybeRelearnAsync(int siteId, string why)
    {
        var settings = await settingsStore.GetAsync();
        if (!settings.AiAutofixReading || !await ai.AvailableAsync())
            return;
        lock (_fixing)
            if (!_fixing.Add(siteId))
                return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var site = await db.Sites.Include(s => s.Product).FirstOrDefaultAsync(s => s.Id == siteId);
                if (site?.Product == null || site.LastAiFixAt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(Math.Max(1, settings.AiCooldownHours)))
                    return;
                site.LastAiFixAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();

                await notifier.LogAsync(EventLevel.Info, $"{site.Name}: {why}. The AI is looking at the page again…", site.Id);
                var cookies = site.CookiesEncrypted == null ? [] : SiteCookies.Parse(box.Decrypt(site.CookiesEncrypted), site.Url);
                SetupSuggestion s;
                try
                {
                    s = await setup.SuggestAsync(site.Url, site.Product, cookies, site.Id, "autofix-reading", CancellationToken.None, site.RunOnPc);
                }
                catch (Exception e)
                {
                    await notifier.NotifyAsync(NotifyKind.Error, EventLevel.Warning, $"The AI couldn't look at {site.Name}: {e.Message.Split('\n')[0]}", site.Id, site.Url);
                    return;
                }

                if (s.RightProduct == false)
                    await notifier.NotifyAsync(NotifyKind.Error, EventLevel.Warning,
                        $"The AI thinks {site.Name} isn't the {site.Product.Name} page any more: {s.Notes}", site.Id, site.Url);
                if (!s.Works)
                {
                    await notifier.NotifyAsync(NotifyKind.Error, EventLevel.Warning,
                        $"The AI couldn't find a way to read {site.Name}: {s.Error ?? string.Join(", ", s.Result?.Notes ?? [])}. {s.Notes}", site.Id, site.Url);
                    return;
                }

                var before = $"{site.Kind}{(site.PriceSelector != null ? $", price {site.PriceSelector}" : "")}";
                site.Kind = s.Kind;
                site.PriceSelector = s.PriceSelector;
                site.TitleSelector = s.TitleSelector;
                site.InStockSelector = s.InStockSelector;
                site.OutOfStockText = s.OutOfStockText;
                site.UnreadableCount = 0;
                await db.SaveChangesAsync();
                await notifier.LogAsync(EventLevel.Good,
                    $"AI fixed how {site.Name} is read: now {s.Kind}{(s.PriceSelector != null ? $", price {s.PriceSelector}" : ", structured data")} "
                    + $"(was {before}). It found {s.Result!.Price:0.00}, {(s.Result.InStock == true ? "in stock" : "out of stock")}. {s.Notes}", site.Id);
            }
            catch (Exception e)
            {
                log.LogError(e, "AI re-learning site {Site} failed", siteId);
            }
            finally
            {
                lock (_fixing)
                    _fixing.Remove(siteId);
            }
        });
    }
}

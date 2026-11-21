using DeficitSnatcher.Api.Ai;
using DeficitSnatcher.Api.Buying;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Settings;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// One check of one site: fetch and read the page, keep what changed, tell the owner about what matters (in stock in
/// range, a lower price, a site that keeps failing) and buy when it's a match and auto-buy is on.
/// </summary>
public sealed class CheckService(IServiceScopeFactory scopes, PageFetcher fetcher, SettingsStore settingsStore, SecretBox box,
    Notifier notifier, Buyer buyer, ScanFollowUp followUp, Backoff backoff, Alarms.AlarmService alarms, BrowserPool browsers, ILogger<CheckService> log)
{
    /// <summary>The most one page fetch may take, whatever happens in the browser (room for a new session's home-page
    /// visit and a Cloudflare wait on top of the page's own timeout).</summary>
    public static TimeSpan FetchLimit(BotSettings settings, Site? site = null) =>
        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds + 90)
        // A store with a captcha the owner may be asked to solve: their time to answer on top.
        + (site != null && Uri.TryCreate(site.Url, UriKind.Absolute, out var u) && Captchas.For(u) != null ? Captchas.Wait : TimeSpan.Zero);

    public sealed record CheckOutcome(PageResult? Result, BuyRules.Verdict? Verdict, string? Error, string? BuyMessage, long? ScanId = null,
        int? PurchaseId = null);

    /// <summary>Checks the site and keeps a scan of it, whatever it found. <paramref name="manual"/>: Check now on the web.</summary>
    public async Task<CheckOutcome> CheckAsync(int siteId, CancellationToken ct, bool manual = false)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = await db.Sites.Include(s => s.Product).FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site?.Product == null)
            return new(null, null, "No such site.", null);
        var product = site.Product;
        var settings = await settingsStore.GetAsync(ct);
        var now = DateTimeOffset.UtcNow;
        // It rests after the store blocked it (not a failure): blocks wear off with time, not with checking again.
        if (!manual && backoff.RestingUntil(site.Id) is { } until)
        {
            site.LastError = $"Resting: the store blocked the last checks, so it isn't asked again until {Times.At(until, "time")} (blocks wear off "
                + "with time; checking again right away keeps them on). Check now still checks.";
            await db.SaveChangesAsync(CancellationToken.None);
            return new(null, null, null, null);
        }
        const string worker = "server";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        PageResult result;
        try
        {
            var cookies = site.CookiesEncrypted == null ? [] : SiteCookies.Parse(box.Decrypt(site.CookiesEncrypted), site.Url);
            // A hard limit on top of the page's own timeouts: a browser that stops answering mustn't hold the check forever.
            result = await fetcher.FetchAsync(site, cookies, settings, ct).WaitAsync(FetchLimit(settings, site), ct);
            browsers.CamoufoxAnswered();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            if (e is TimeoutException)
                browsers.CamoufoxHung();
            else if (e is BlockedException)
                browsers.CamoufoxAnswered();
            var message = e switch
            {
                TaskCanceledException => $"Timed out after {settings.RequestTimeoutSeconds} s.",
                TimeoutException => $"The page didn't load in {FetchLimit(settings).TotalSeconds:0} s (the browser was too slow or stuck); tried again next check.",
                _ => e.Message.Split('\n')[0],
            };
            // Blocked: the page rests for a while (each block in a row, longer).
            if (e is BlockedException)
            {
                var rest = backoff.Blocked(site.Id, (e as BlockedException)?.RetryAfter);
                message += $" It rests for {(rest.TotalHours >= 1 ? $"{rest.TotalHours:0.#} h" : $"{rest.TotalMinutes:0} min")}.";
                if (backoff.MinGap(site.Id) is { } gap)
                    message += $" The store seems to rate-limit: its checks are now spaced at least {gap.TotalMinutes:0} min apart.";
            }
            site.LastCheckedAt = now;
            site.LastError = message;
            site.ConsecutiveErrors++;
            var failed = new Scan
            {
                SiteId = site.Id,
                ProductId = product.Id,
                At = now,
                DurationMs = (int)watch.ElapsedMilliseconds,
                Outcome = ScanOutcome.Error,
                Verdict = "Check failed",
                Error = Cut(message, 2000),
                Notes = $"{site.Kind} · failed {site.ConsecutiveErrors} in a row",
                Manual = manual,
                Worker = worker,
            };
            db.Scans.Add(failed);
            await db.SaveChangesAsync(CancellationToken.None);
            log.LogInformation(e, "Check of {Site} failed after {ElapsedMs} ms: {Error}", site.Name, watch.ElapsedMilliseconds, message);
            // Not after a block: the screenshot would be one more visit to a store that just turned it away.
            if (settings.ScanSnapshots != "Off" && e is not BlockedException)
                followUp.Snapshot(failed.Id, site.Url, site.CookiesEncrypted, "The check failed", null, null,
                    new Site { Id = site.Id, Url = site.Url });
            // Keeps failing in a way a different reader might get past (blocked, timed out, changed page), not a dead link.
            // (Not when the store's bot protection blocks it: re-reading the page can't get past that.)
            if (site.ConsecutiveErrors >= 3 && !message.Contains("404") && e is not (BlockedException or BrowserEngineException))
                await followUp.MaybeRelearnAsync(site.Id, $"{site.ConsecutiveErrors} checks in a row failed ({message})");
            if (site.ConsecutiveErrors == settings.ErrorThreshold)
                await notifier.NotifyAsync(NotifyKind.PageFailing, EventLevel.Warning,
                    $"{site.Name} failed {site.ConsecutiveErrors} checks in a row: {message}", site.Id, site.Url);
            return new(null, null, message, null, failed.Id);
        }
        var duration = (int)watch.ElapsedMilliseconds;
        backoff.Succeeded(site.Id);

        if (site.ConsecutiveErrors >= settings.ErrorThreshold)
            await notifier.NotifyAsync(NotifyKind.PageRecovered, EventLevel.Info, $"{site.Name} is working again.", site.Id);

        var before = BuyRules.Evaluate(product, site, new PageResult { Price = site.LastPrice, InStock = site.LastInStock, Title = site.LastTitle });
        var verdict = BuyRules.Evaluate(product, site, result);
        // Just became a match: a second look before alerting or buying. Pages that draw their stock in script can read
        // "in stock" from their hidden product data before the "Out of stock" message appears; a page that flips back and
        // forth would otherwise alert over and over. A second look that fails (blocked, timed out) keeps the first reading.
        if (verdict.Match && !before.Match)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4 + Random.Shared.Next(4)), ct);
                var cookies = site.CookiesEncrypted == null ? [] : SiteCookies.Parse(box.Decrypt(site.CookiesEncrypted), site.Url);
                var second = await fetcher.FetchAsync(site, cookies, settings, ct).WaitAsync(FetchLimit(settings, site), ct);
                if (!BuyRules.Evaluate(product, site, second).Match)
                {
                    log.LogInformation("Check of {Site}: an in-stock reading wasn't confirmed by a second look ({Verdict}); kept the second",
                        site.Name, BuyRules.Evaluate(product, site, second).Reason);
                    result = second with { Notes = [.. second.Notes, "a first look read in stock; a second look didn't confirm it"] };
                    verdict = BuyRules.Evaluate(product, site, result);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogInformation("Check of {Site}: second look failed ({Error}); keeping the in-stock reading", site.Name, e.Message.Split('\n')[0]);
            }
        }
        var inStock = result.InStock == true;
        var previousPrice = site.LastPrice;
        var previousInStock = site.LastInStock;
        var firstScan = site.LastCheckedAt == null;

        if (result.Price != site.LastPrice || inStock != site.LastInStock)
        {
            db.PricePoints.Add(new PricePoint { SiteId = site.Id, At = now, Price = result.Price, InStock = inStock });
            site.LastChangedAt = now;
        }
        site.LastCheckedAt = now;
        site.LastPrice = result.Price;
        site.LastInStock = result.InStock;
        site.LastStockDetail = result.StockDetail;
        site.LastTitle = result.Title;
        site.LastVariantId = result.VariantId;
        site.LastError = null;
        site.ConsecutiveErrors = 0;
        site.UnreadableCount = result.Price == null || result.InStock == null ? site.UnreadableCount + 1 : 0;

        var scan = new Scan
        {
            SiteId = site.Id,
            ProductId = product.Id,
            At = now,
            DurationMs = duration,
            Outcome = result.InStock switch { true => ScanOutcome.InStock, false => ScanOutcome.OutOfStock, _ => ScanOutcome.Unknown },
            Price = result.Price,
            TotalPrice = result.Price is { } p ? p + site.ShippingCost : null,
            Title = Cut(result.Title, 300),
            Match = verdict.Match,
            Verdict = Cut(verdict.Reason, 500) ?? "",
            Notes = Cut(string.Join(" · ", new[] { site.Kind.ToString() }.Concat(result.Notes)
                .Concat(result.VariantId != null ? [$"variant {result.VariantId}"] : [])), 1000),
            Manual = manual,
            Worker = worker,
        };
        db.Scans.Add(scan);
        await db.SaveChangesAsync(CancellationToken.None);

        // The scan's snapshot: every scan, or when something worth seeing happened.
        var stockChanged = previousInStock != null && previousInStock != result.InStock;
        var reason = settings.ScanSnapshots == "Every" ? "Every scan"
            : verdict.Match ? "In range"
            : firstScan ? "First check"
            : stockChanged ? (inStock ? "Came into stock" : "Went out of stock")
            : previousPrice != null && result.Price != previousPrice ? $"Price changed from {previousPrice:0.00}"
            : site.UnreadableCount > 0 ? "No price or stock found"
            : null;
        if (reason != null && settings.ScanSnapshots != "Off")
            followUp.Snapshot(scan.Id, site.Url, site.CookiesEncrypted, reason, result.Source, result.SourceType,
                new Site { Id = site.Id, Url = site.Url });
        if (site.UnreadableCount >= 3)
            await followUp.MaybeRelearnAsync(site.Id, $"{site.UnreadableCount} checks in a row found {(result.Price == null ? "no price" : "no stock")}");

        var money = $"{settings.Currency}";
        Task? stockAlert = null;
        if (verdict.Match && !before.Match)
        {
            var text = $"{product.Name} is IN STOCK at {site.Name} for {verdict.TotalPrice:0.00} {money} (your range {product.MinPrice:0.00}–{product.MaxPrice:0.00})"
                + (result.StockDetail is { Length: > 0 } how ? $": {how}." : ".");
            // The phone rings first: every second counts.
            stockAlert = notifier.NotifyAsync(NotifyKind.InStock, EventLevel.Good, text, site.Id, site.Url);
            await alarms.RaiseAsync(product.Name, site.Name, verdict.TotalPrice, site.Url, text, site.Id);
        }
        // Came into stock, but not in the range: worth knowing (e.g. the price went up). Never for going out of stock.
        else if (inStock && previousInStock != true && !firstScan)
            await notifier.NotifyAsync(NotifyKind.InStockOutOfRange, EventLevel.Info,
                $"{product.Name} is in stock at {site.Name}{(result.Price is { } p2 ? $" for {p2 + site.ShippingCost:0.00} {money}" : "")}, but not bought: {verdict.Reason}.",
                site.Id, site.Url);
        // A lower price while it can be bought (out of stock prices don't matter).
        else if (inStock && previousPrice is { } old && result.Price is { } price && price < old)
            await notifier.NotifyAsync(NotifyKind.PriceDrop, EventLevel.Info,
                $"{product.Name} at {site.Name}: price dropped {old:0.00} → {price:0.00} {money}, in stock.", site.Id, site.Url);
        else if (!verdict.Match && before.Match)
            await notifier.LogAsync(EventLevel.Info, $"{product.Name} at {site.Name} no longer a match: {verdict.Reason}.", site.Id);

        string? buyMessage = null;
        int? purchaseId = null;
        if (verdict.Match && !product.AutoBuyEnabled)
            scan.Action = "Auto-buy is off for this product: only notified.";
        else if (verdict.Match && product.AutoBuyEnabled && product.Enabled)
        {
            var trigger = $"{(manual ? "Check now" : "Scheduled check")} #{scan.Id} at {Times.At(now)}: {verdict.Reason}"
                + $"\nListing: {result.Title} · price {result.Price:0.00}{(site.ShippingCost > 0 ? $" + {site.ShippingCost:0.00} shipping" : "")}"
                + $"{(result.VariantId != null ? $" · variant {result.VariantId}" : "")} · read from {string.Join(", ", result.Notes)}";
            var outcome = await buyer.BuyAsync(site.Id, verdict.TotalPrice!.Value, manual: false, forceDryRun: false, trigger, ct);
            buyMessage = outcome.Message;
            purchaseId = outcome.PurchaseId;
            scan.Action = Cut(outcome.Started ? outcome.Message : "Not buying: " + outcome.Message, 1000);
            scan.PurchaseId = outcome.PurchaseId;
            // Why it didn't buy, once per time it comes into stock (not every check).
            if (!outcome.Started && !before.Match)
                await notifier.LogAsync(EventLevel.Warning, $"Not buying at {site.Name}: {outcome.Message}", site.Id);
        }
        if (stockAlert != null)
            await stockAlert;
        if (scan.Action != null)
            await db.SaveChangesAsync(CancellationToken.None);
        return new(result, verdict, null, buyMessage, scan.Id, purchaseId);
    }

    private static string? Cut(string? s, int n) => s == null ? null : s.Length > n ? s[..n] : s;

    /// <summary>Checks a site as set on the edit form, without saving anything: the form's Test button.</summary>
    public async Task<CheckOutcome> ProbeAsync(Site site, Product product, string? cookiesText, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        try
        {
            var result = await fetcher.FetchAsync(site, SiteCookies.Parse(cookiesText, site.Url), settings, ct);
            return new(result, BuyRules.Evaluate(product, site, result), null, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new(null, null, e is TaskCanceledException ? $"Timed out after {settings.RequestTimeoutSeconds} s." : e.Message.Split('\n')[0], null);
        }
    }
}

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
    Notifier notifier, Buyer buyer, ILogger<CheckService> log)
{
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
        var watch = System.Diagnostics.Stopwatch.StartNew();

        PageResult result;
        try
        {
            var cookies = site.CookiesEncrypted == null ? [] : SiteCookies.Parse(box.Decrypt(site.CookiesEncrypted), site.Url);
            result = await fetcher.FetchAsync(site, cookies, settings, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var message = e is TaskCanceledException ? $"Timed out after {settings.RequestTimeoutSeconds} s." : e.Message.Split('\n')[0];
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
            };
            db.Scans.Add(failed);
            await db.SaveChangesAsync(CancellationToken.None);
            log.LogInformation("Check of {Site} failed: {Error}", site.Name, message);
            if (site.ConsecutiveErrors == settings.ErrorThreshold)
                await notifier.NotifyAsync(NotifyKind.Error, EventLevel.Warning,
                    $"{site.Name} failed {site.ConsecutiveErrors} checks in a row: {message}", site.Id, site.Url);
            return new(null, null, message, null, failed.Id);
        }
        var duration = (int)watch.ElapsedMilliseconds;

        if (site.ConsecutiveErrors >= settings.ErrorThreshold)
            await notifier.NotifyAsync(NotifyKind.Error, EventLevel.Info, $"{site.Name} is working again.", site.Id);

        var before = BuyRules.Evaluate(product, site, new PageResult { Price = site.LastPrice, InStock = site.LastInStock, Title = site.LastTitle });
        var verdict = BuyRules.Evaluate(product, site, result);
        var inStock = result.InStock == true;
        var previousPrice = site.LastPrice;

        if (result.Price != site.LastPrice || inStock != site.LastInStock)
        {
            db.PricePoints.Add(new PricePoint { SiteId = site.Id, At = now, Price = result.Price, InStock = inStock });
            site.LastChangedAt = now;
        }
        site.LastCheckedAt = now;
        site.LastPrice = result.Price;
        site.LastInStock = result.InStock;
        site.LastTitle = result.Title;
        site.LastVariantId = result.VariantId;
        site.LastError = null;
        site.ConsecutiveErrors = 0;

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
        };
        db.Scans.Add(scan);
        await db.SaveChangesAsync(CancellationToken.None);

        var money = $"{settings.Currency}";
        if (verdict.Match && !before.Match)
            await notifier.NotifyAsync(NotifyKind.InStock, EventLevel.Good,
                $"{product.Name} is IN STOCK at {site.Name} for {verdict.TotalPrice:0.00} {money} (your range {product.MinPrice:0.00}–{product.MaxPrice:0.00}).",
                site.Id, site.Url);
        else if (previousPrice is { } old && result.Price is { } price && price < old)
            await notifier.NotifyAsync(NotifyKind.PriceDrop, EventLevel.Info,
                $"{product.Name} at {site.Name}: price dropped {old:0.00} → {price:0.00} {money}{(inStock ? "" : " (out of stock)")}.", site.Id, site.Url);
        else if (!verdict.Match && before.Match)
            await notifier.LogAsync(EventLevel.Info, $"{product.Name} at {site.Name} no longer a match: {verdict.Reason}.", site.Id);

        string? buyMessage = null;
        int? purchaseId = null;
        if (verdict.Match && !site.AutoBuy)
            scan.Action = "Auto-buy is off for this site: only notified.";
        else if (verdict.Match && site.AutoBuy && product.Enabled)
        {
            var trigger = $"{(manual ? "Check now" : "Scheduled check")} #{scan.Id} at {now:yyyy-MM-dd HH:mm:ss} UTC: {verdict.Reason}"
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

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Buying;

/// <summary>
/// Buys: runs a site's checkout script in headless Chromium, after checking every limit (auto-buy armed, units left
/// to buy for the product, the total budget, the retry wait). The limits are checked and the purchase recorded under
/// one lock, so two sites coming into stock together can't buy more than the product's limit between them.
/// </summary>
public sealed partial class Buyer(IServiceScopeFactory scopes, SettingsStore settingsStore, SecretBox box, BrowserPool browsers,
    Notifier notifier, ILogger<Buyer> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public sealed record Outcome(bool Started, string Message, int? PurchaseId = null, PurchaseStatus? Status = null);

    /// <summary>
    /// A check found the site in stock in range. <paramref name="manual"/>: started from the web (doesn't need
    /// auto-buy armed or the site's AutoBuy, and ignores the retry wait). <paramref name="forceDryRun"/>: stop before
    /// the final steps whatever the settings say.
    /// </summary>
    public async Task<Outcome> BuyAsync(int siteId, decimal price, bool manual, bool forceDryRun, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var dryRun = forceDryRun || settings.DryRun;
        Site site;
        Product product;
        List<CheckoutStep> steps;
        int purchaseId;

        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            site = await db.Sites.Include(s => s.Product).FirstOrDefaultAsync(s => s.Id == siteId, ct)
                ?? throw new InvalidOperationException("No such site.");
            product = site.Product!;
            var now = DateTimeOffset.UtcNow;

            if (!manual)
            {
                if (!settings.AutoBuyArmed)
                    return new(false, "Auto-buy isn't armed.");
                if (!site.AutoBuy)
                    return new(false, "Auto-buy is off for this site.");
                if (site.LastBuyAttemptAt is { } last && now - last < TimeSpan.FromMinutes(settings.BuyRetryMinutes))
                    return new(false, "Waiting before trying this site again.");
            }

            steps = CheckoutStep.Parse(site.CheckoutSteps);
            if (steps.Count == 0)
                return new(false, "This site has no checkout steps.");
            if (CheckoutStep.Validate(steps) is { } invalid)
                return new(false, invalid);
            if (!dryRun && !steps.Any(s => s.Final))
                return new(false, "The checkout has no final step (the one that places the order), so it can't buy.");

            if (!dryRun)
            {
                var quantity = product.QuantityPerOrder;
                var bought = await db.Purchases
                    .Where(p => p.ProductId == product.Id && (p.Status == PurchaseStatus.Succeeded || p.Status == PurchaseStatus.Running))
                    .SumAsync(p => (int?)p.Quantity, ct) ?? 0;
                if (bought + quantity > product.MaxQuantity)
                    return new(false, $"Already bought {bought} of {product.MaxQuantity} {product.Name}.");
                if (settings.TotalBudget > 0)
                {
                    var spent = await db.Purchases.Where(p => p.Status == PurchaseStatus.Succeeded || p.Status == PurchaseStatus.Running)
                        .Select(p => new { p.Price, p.Quantity }).ToListAsync(ct);
                    var total = spent.Sum(p => p.Price * p.Quantity);
                    if (total + price * quantity > settings.TotalBudget)
                        return new(false, $"Over budget: spent {total:0.00} of {settings.TotalBudget:0.00}.");
                }
            }

            var purchase = new Purchase
            {
                SiteId = site.Id,
                ProductId = product.Id,
                StartedAt = now,
                Price = price,
                Quantity = product.QuantityPerOrder,
                Status = PurchaseStatus.Running,
                Message = dryRun ? "Dry run…" : "Checking out…",
            };
            db.Purchases.Add(purchase);
            site.LastBuyAttemptAt = now;
            await db.SaveChangesAsync(ct);
            purchaseId = purchase.Id;
        }
        finally
        {
            _gate.Release();
        }

        await notifier.LogAsync(EventLevel.Info, $"{(dryRun ? "Dry-run checkout" : "Buying")} {product.Name} at {site.Name} for {price:0.00}…", site.Id);
        _ = Task.Run(() => RunAsync(purchaseId, site, product, steps, price, dryRun, settings), CancellationToken.None);
        return new(true, dryRun ? "Dry-run checkout started." : "Checkout started.", purchaseId, PurchaseStatus.Running);
    }

    private async Task RunAsync(int purchaseId, Site site, Product product, List<CheckoutStep> steps, decimal price, bool dryRun, BotSettings settings)
    {
        var logText = new StringBuilder();
        PurchaseStatus status;
        string message;
        byte[]? screenshot = null;
        try
        {
            var profile = await settingsStore.GetProfileAsync();
            var values = profile.Placeholders();
            var url = new Uri(site.Url);
            values["url"] = site.Url;
            values["origin"] = url.GetLeftPart(UriPartial.Authority);
            values["variantId"] = site.LastVariantId ?? site.VariantId ?? "";
            values["quantity"] = product.QuantityPerOrder.ToString();
            values["price"] = price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

            var cookies = site.CookiesEncrypted == null ? [] : SiteCookies.Parse(box.Decrypt(site.CookiesEncrypted), site.Url);
            await using var context = await browsers.NewContextAsync(settings.UserAgent, cookies, CancellationToken.None);
            var page = await context.NewPageAsync();
            (status, message) = (PurchaseStatus.Succeeded, "Order placed.");
            try
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    if (step.Final && dryRun)
                    {
                        logText.AppendLine($"{i + 1}. {Describe(step, values)}: not run (dry run stops here)");
                        (status, message) = (PurchaseStatus.DryRun, $"Dry run reached step {i + 1} ({step.Action}) without errors; the order wasn't placed.");
                        break;
                    }
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        await RunStepAsync(page, step, values);
                        logText.AppendLine($"{i + 1}. {Describe(step, values)}: ok ({watch.ElapsedMilliseconds} ms)");
                    }
                    catch (Exception e) when (e is PlaywrightException or TimeoutException or InvalidOperationException)
                    {
                        var first = e.Message.Split('\n')[0];
                        logText.AppendLine($"{i + 1}. {Describe(step, values)}: FAILED{(step.Optional ? " (optional, carried on)" : "")}: {first}");
                        if (!step.Optional)
                        {
                            (status, message) = (PurchaseStatus.Failed, $"Step {i + 1} ({step.Action}) failed: {first}");
                            break;
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    screenshot = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png, Timeout = 10000 });
                }
                catch (Exception e)
                {
                    logText.AppendLine($"(no screenshot: {e.Message.Split('\n')[0]})");
                }
                // Without the query: a form sent by GET can put what was typed (the card) in it.
                logText.AppendLine($"Ended on {page.Url.Split('?', '#')[0]}");
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Checkout {PurchaseId} crashed", purchaseId);
            (status, message) = (PurchaseStatus.Failed, "Checkout couldn't run: " + e.Message.Split('\n')[0]);
        }

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var purchase = await db.Purchases.FirstAsync(p => p.Id == purchaseId);
            purchase.Status = status;
            purchase.Message = message;
            purchase.Log = logText.ToString();
            purchase.Screenshot = screenshot;
            purchase.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var (level, text) = status switch
        {
            PurchaseStatus.Succeeded => (EventLevel.Good, $"BOUGHT {product.Name} at {site.Name} for {price:0.00} {settings.Currency}! {message}"),
            PurchaseStatus.DryRun => (EventLevel.Info, $"Dry run for {product.Name} at {site.Name} ({price:0.00} {settings.Currency}): {message}"),
            _ => (EventLevel.Error, $"Couldn't buy {product.Name} at {site.Name} ({price:0.00} {settings.Currency}): {message}"),
        };
        await notifier.NotifyAsync(NotifyKind.Purchase, level, text, site.Id, site.Url);
    }

    private static async Task RunStepAsync(IPage page, CheckoutStep step, Dictionary<string, string> values)
    {
        var timeout = (float)(step.TimeoutMs ?? 15000);
        string V() => Fill(step.Value ?? "", values);
        ILocator L() => (step.Frame is { Length: > 0 } frame ? page.FrameLocator(frame).Locator(step.Selector!) : page.Locator(step.Selector!)).First;

        switch (step.Action)
        {
            case "goto":
                await page.GotoAsync(V(), new() { Timeout = Math.Max(timeout, 30000), WaitUntil = WaitUntilState.DOMContentLoaded });
                break;
            case "click":
                await L().ClickAsync(new() { Timeout = timeout });
                break;
            case "fill":
                await L().FillAsync(V(), new() { Timeout = timeout });
                break;
            case "type":
                await L().PressSequentiallyAsync(V(), new() { Timeout = timeout, Delay = 40 });
                break;
            case "select":
                // Matches an option's value or its visible text.
                await L().SelectOptionAsync(V(), new() { Timeout = timeout });
                break;
            case "check":
                await L().CheckAsync(new() { Timeout = timeout });
                break;
            case "press":
                await L().PressAsync(V(), new() { Timeout = timeout });
                break;
            case "wait":
                await page.WaitForTimeoutAsync(int.Parse(V()));
                break;
            case "waitFor":
                await L().WaitForAsync(new() { Timeout = timeout, State = WaitForSelectorState.Visible });
                break;
            case "waitForUrl":
                var part = V();
                await page.WaitForURLAsync(u => u.Contains(part, StringComparison.OrdinalIgnoreCase), new() { Timeout = timeout });
                break;
            case "expectText":
                await page.GetByText(V()).First.WaitForAsync(new() { Timeout = timeout, State = WaitForSelectorState.Visible });
                break;
            default:
                throw new InvalidOperationException($"Unknown action {step.Action}");
        }
    }

    /// <summary>Replaces {{name}} placeholders; an unknown or empty one fails the step rather than typing nothing.</summary>
    private static string Fill(string text, Dictionary<string, string> values) => Placeholder().Replace(text, m =>
    {
        var name = m.Groups[1].Value;
        if (!values.TryGetValue(name, out var v))
            throw new InvalidOperationException($"Unknown placeholder {{{{{name}}}}}.");
        if (string.IsNullOrEmpty(v) && name != "address2")
            throw new InvalidOperationException($"{{{{{name}}}}} is empty: fill it in Settings > Checkout profile.");
        return v;
    });

    /// <summary>The step for the log, with card details never written out.</summary>
    private static string Describe(CheckoutStep step, Dictionary<string, string> values)
    {
        var target = step.Selector is { Length: > 0 } sel ? $" {sel}" + (step.Frame is { Length: > 0 } f ? $" in {f}" : "") : "";
        var value = step.Value is { Length: > 0 } v
            ? " \"" + Placeholder().Replace(v, m => m.Groups[1].Value.StartsWith("card.", StringComparison.OrdinalIgnoreCase) || m.Groups[1].Value is "phone"
                ? "•••"
                : values.TryGetValue(m.Groups[1].Value, out var x) ? x : m.Value) + "\""
            : "";
        return $"{step.Action}{target}{value}";
    }

    [GeneratedRegex(@"\{\{\s*([\w.]+)\s*\}\}")]
    private static partial Regex Placeholder();
}

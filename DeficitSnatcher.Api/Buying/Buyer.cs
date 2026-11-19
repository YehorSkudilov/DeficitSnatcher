using System.Diagnostics;
using System.Text;
using System.Text.Json;
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

    /// <summary>Checkouts that count against the units to buy and the budget.</summary>
    public static readonly PurchaseStatus[] CountsAsBought = [PurchaseStatus.Succeeded, PurchaseStatus.Running, PurchaseStatus.Interrupted];

    public sealed record Outcome(bool Started, string Message, int? PurchaseId = null, PurchaseStatus? Status = null);

    /// <summary>
    /// A check found the site in stock in range. <paramref name="manual"/>: started from the web (doesn't need
    /// auto-buy armed or the site's AutoBuy, and ignores the retry wait). <paramref name="forceDryRun"/>: stop before
    /// the final steps whatever the settings say. <paramref name="trigger"/>: what led to it, kept with the checkout.
    /// </summary>
    public async Task<Outcome> BuyAsync(int siteId, decimal price, bool manual, bool forceDryRun, string trigger, CancellationToken ct)
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
                    return new(false, $"Waiting before trying this site again (last try {(now - last).TotalMinutes:0} min ago, retry after {settings.BuyRetryMinutes}).");
            }

            steps = CheckoutStep.Parse(site.CheckoutSteps);
            if (steps.Count == 0)
                return new(false, "This site has no checkout steps.");
            if (CheckoutStep.Validate(steps) is { } invalid)
                return new(false, invalid);
            if (!dryRun && !steps.Any(s => s.Final))
                return new(false, "The checkout has no final step (the one that places the order), so it can't buy.");

            var quantity = product.QuantityPerOrder;
            var bought = await db.Purchases
                .Where(p => p.ProductId == product.Id && CountsAsBought.Contains(p.Status) && !p.DryRun)
                .SumAsync(p => (int?)p.Quantity, ct) ?? 0;
            var spent = (await db.Purchases.Where(p => CountsAsBought.Contains(p.Status) && !p.DryRun)
                .Select(p => new { p.Price, p.Quantity }).ToListAsync(ct)).Sum(p => p.Price * p.Quantity);
            if (!dryRun)
            {
                if (bought + quantity > product.MaxQuantity)
                    return new(false, $"Already bought {bought} of {product.MaxQuantity} {product.Name}.");
                if (settings.TotalBudget > 0 && spent + price * quantity > settings.TotalBudget)
                    return new(false, $"Over budget: spent {spent:0.00} of {settings.TotalBudget:0.00}, this would add {price * quantity:0.00}.");
            }

            var context = new StringBuilder()
                .AppendLine(trigger)
                .AppendLine($"Mode: {(dryRun ? "dry run (stops before the final step)" : "REAL purchase")}{(manual ? ", started by hand from the web" : ", started automatically")}")
                .AppendLine($"Auto-buy armed: {(settings.AutoBuyArmed ? "yes" : "no")} · site auto-buy: {(site.AutoBuy ? "on" : "off")}")
                .AppendLine($"Price {price:0.00} {settings.Currency} · range {product.MinPrice:0.00}–{product.MaxPrice:0.00} · {quantity} per order")
                .AppendLine($"Bought before: {bought} of {product.MaxQuantity} · spent {spent:0.00}{(settings.TotalBudget > 0 ? $" of budget {settings.TotalBudget:0.00}" : " (no budget)")}")
                .AppendLine($"Page: {site.Url}")
                .Append($"Steps: {steps.Count}, final: {string.Join(", ", steps.Select((s, i) => (s, i)).Where(x => x.s.Final).Select(x => x.i + 1))}")
                .ToString();

            var purchase = new Purchase
            {
                SiteId = site.Id,
                ProductId = product.Id,
                StartedAt = now,
                Price = price,
                Quantity = quantity,
                Status = PurchaseStatus.Running,
                Message = dryRun ? "Dry run…" : "Checking out…",
                DryRun = dryRun,
                Manual = manual,
                Context = context,
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

        await notifier.LogAsync(EventLevel.Info, $"{(dryRun ? "Dry-run checkout" : "Buying")} {product.Name} at {site.Name} for {price:0.00} (checkout #{purchaseId})…", site.Id);
        _ = Task.Run(() => RunAsync(purchaseId, site, product, steps, price, dryRun, settings), CancellationToken.None);
        return new(true, $"{(dryRun ? "Dry-run checkout" : "Checkout")} #{purchaseId} started.", purchaseId, PurchaseStatus.Running);
    }

    /// <summary>One step as it ran, for the checkout's timeline.</summary>
    public sealed record StepRecord(
        int Index,
        string Action,
        string Description,
        /// <summary>ok, failed, skipped (optional and failed), notRun (after a failure, or final in a dry run).</summary>
        string Status,
        DateTimeOffset? StartedAt,
        long? Ms,
        string? Url,
        string? Error,
        long? ShotId,
        bool Final,
        bool Optional);

    private async Task RunAsync(int purchaseId, Site site, Product product, List<CheckoutStep> steps, decimal price, bool dryRun, BotSettings settings)
    {
        var logText = new StringBuilder();
        var records = new List<StepRecord>();
        var browserLog = new List<string>();
        var started = DateTimeOffset.UtcNow;
        PurchaseStatus status = PurchaseStatus.Failed;
        string message = "";
        string? error = null, html = null, finalUrl = null;
        Func<string, string> redact = s => s;

        void Log(string line) => logText.AppendLine($"[{(DateTimeOffset.UtcNow - started).TotalSeconds,6:0.00}s] {line}");
        void Browser(string line)
        {
            lock (browserLog)
                if (browserLog.Count < 500)
                    browserLog.Add($"[{(DateTimeOffset.UtcNow - started).TotalSeconds,6:0.00}s] {line}");
        }

        try
        {
            var profile = await settingsStore.GetProfileAsync();
            var values = profile.Placeholders();
            redact = s => Redact(s, profile);
            var url = new Uri(site.Url);
            values["url"] = site.Url;
            values["origin"] = url.GetLeftPart(UriPartial.Authority);
            values["variantId"] = site.LastVariantId ?? site.VariantId ?? "";
            values["quantity"] = product.QuantityPerOrder.ToString();
            values["price"] = price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

            var cookies = site.CookiesEncrypted == null ? [] : SiteCookies.Parse(box.Decrypt(site.CookiesEncrypted), site.Url);
            Log($"Opening a browser ({cookies.Count} cookies, user agent: {settings.UserAgent})");
            await using var context = await browsers.NewContextAsync(settings.UserAgent, cookies, CancellationToken.None);
            var page = await context.NewPageAsync();
            page.Console += (_, m) =>
            {
                if (m.Type is "error" or "warning")
                    Browser($"console.{m.Type}: {Short(m.Text, 500)}");
            };
            page.PageError += (_, e) => Browser($"page error: {Short(e, 500)}");
            page.RequestFailed += (_, r) => Browser($"request failed: {r.Method} {NoQuery(r.Url)}: {r.Failure}");
            page.Response += (_, r) =>
            {
                if (r.Status >= 400)
                    Browser($"HTTP {r.Status} {r.Request.Method} {NoQuery(r.Url)} ({r.Request.ResourceType})");
            };
            page.FrameNavigated += (_, f) =>
            {
                if (f.ParentFrame == null)
                    Browser($"navigated to {NoQuery(f.Url)}");
            };

            (status, message) = (PurchaseStatus.Succeeded, "Order placed.");
            var stopped = false;
            try
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    var description = Describe(step, values);
                    if (stopped)
                    {
                        records.Add(new(i, step.Action, description, "notRun", null, null, null, null, null, step.Final, step.Optional));
                        continue;
                    }
                    if (step.Final && dryRun)
                    {
                        Log($"{i + 1}. {description}: not run (dry run stops here)");
                        records.Add(new(i, step.Action, description, "notRun", null, null, NoQuery(page.Url), "Dry run: the final step isn't run.", null, true, step.Optional));
                        (status, message) = (PurchaseStatus.DryRun, $"Dry run reached step {i + 1} ({step.Action}) without errors; the order wasn't placed.");
                        stopped = true;
                        continue;
                    }

                    var at = DateTimeOffset.UtcNow;
                    var watch = Stopwatch.StartNew();
                    string stepStatus;
                    string? stepError = null;
                    try
                    {
                        await RunStepAsync(page, step, values);
                        stepStatus = "ok";
                        Log($"{i + 1}. {description}: ok ({watch.ElapsedMilliseconds} ms) on {NoQuery(page.Url)}");
                    }
                    catch (Exception e) when (e is PlaywrightException or TimeoutException or InvalidOperationException)
                    {
                        stepError = redact(e.Message);
                        stepStatus = step.Optional ? "skipped" : "failed";
                        Log($"{i + 1}. {description}: FAILED{(step.Optional ? " (optional, carried on)" : "")} after {watch.ElapsedMilliseconds} ms: {stepError.Split('\n')[0]}");
                        if (!step.Optional)
                        {
                            (status, message) = (PurchaseStatus.Failed, $"Step {i + 1} ({step.Action}{(step.Selector is { Length: > 0 } sel ? " " + sel : "")}) failed: {stepError.Split('\n')[0]}");
                            error = $"Step {i + 1}: {description}\nPage: {NoQuery(page.Url)}\n\n{stepError}";
                            stopped = true;
                        }
                    }
                    long? shotId = null;
                    if (settings.StepScreenshots || stepStatus == "failed")
                        shotId = await ShotAsync(purchaseId, page, i, $"After step {i + 1}: {description} ({stepStatus})", fullPage: false);
                    records.Add(new(i, step.Action, description, stepStatus, at, watch.ElapsedMilliseconds, NoQuery(page.Url), stepError, shotId,
                        step.Final, step.Optional));
                }
            }
            finally
            {
                finalUrl = NoQuery(page.Url);
                Log($"Ended on {finalUrl}");
                await ShotAsync(purchaseId, page, null, $"Where it ended: {finalUrl}", fullPage: true);
                try
                {
                    html = redact(await page.ContentAsync());
                }
                catch (Exception e)
                {
                    Log($"(couldn't save the page's HTML: {e.Message.Split('\n')[0]})");
                }
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Checkout {PurchaseId} crashed", purchaseId);
            (status, message) = (PurchaseStatus.Failed, "Checkout couldn't run: " + redact(e.Message.Split('\n')[0]));
            error = redact(e.ToString());
            Log("CRASHED: " + error);
        }
        Log($"Result: {status}: {message}");

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var purchase = await db.Purchases.FirstAsync(p => p.Id == purchaseId);
            purchase.Status = status;
            purchase.Message = message;
            purchase.Log = redact(logText.ToString());
            purchase.Steps = JsonSerializer.Serialize(records, JsonOptions);
            purchase.Error = error;
            lock (browserLog)
                purchase.BrowserLog = redact(string.Join("\n", browserLog));
            purchase.PageHtml = html == null ? null : box.Encrypt(html.Length > 3_000_000 ? html[..3_000_000] : html);
            purchase.FinalUrl = finalUrl;
            purchase.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var link = notifier.Link($"purchase/{purchaseId}");
        var (level, text) = status switch
        {
            PurchaseStatus.Succeeded => (EventLevel.Good, $"BOUGHT {product.Name} at {site.Name} for {price:0.00} {settings.Currency}! {message} (checkout #{purchaseId})"),
            PurchaseStatus.DryRun => (EventLevel.Info, $"Dry run #{purchaseId} for {product.Name} at {site.Name} ({price:0.00} {settings.Currency}): {message}"),
            _ => (EventLevel.Error, $"Couldn't buy {product.Name} at {site.Name} ({price:0.00} {settings.Currency}), checkout #{purchaseId}: {message}"),
        };
        await notifier.NotifyAsync(NotifyKind.Purchase, level, text, site.Id, link ?? site.Url);
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A screenshot of the page, kept encrypted (it can show what was typed). Never fails the checkout.</summary>
    private async Task<long?> ShotAsync(int purchaseId, IPage page, int? stepIndex, string label, bool fullPage)
    {
        try
        {
            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = fullPage ? 75 : 60,
                FullPage = fullPage,
                Timeout = 8000,
                Animations = ScreenshotAnimations.Disabled,
            });
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var shot = new PurchaseShot
            {
                PurchaseId = purchaseId,
                StepIndex = stepIndex,
                Label = label.Length > 500 ? label[..500] : label,
                ContentType = "image/jpeg",
                Data = box.EncryptBytes(bytes),
                At = DateTimeOffset.UtcNow,
            };
            db.PurchaseShots.Add(shot);
            await db.SaveChangesAsync();
            return shot.Id;
        }
        catch (Exception e)
        {
            log.LogWarning("Screenshot for checkout {PurchaseId} failed: {Error}", purchaseId, e.Message.Split('\n')[0]);
            return null;
        }
    }

    /// <summary>Card number and CVV never end up in the logs, errors or saved HTML.</summary>
    private static string Redact(string text, CheckoutProfile profile)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (profile.CardNumber.Length >= 12)
        {
            text = text.Replace(profile.CardNumber, "•••• " + profile.CardNumber[^4..]);
            var spaced = string.Join(' ', profile.CardNumber.Chunk(4).Select(c => new string(c)));
            text = text.Replace(spaced, "•••• " + profile.CardNumber[^4..]);
        }
        if (profile.CardCvv.Length >= 3)
            text = text.Replace($"\"{profile.CardCvv}\"", "\"•••\"").Replace($"'{profile.CardCvv}'", "'•••'");
        return text;
    }

    private static string NoQuery(string url) => url.Split('?', '#')[0];

    private static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

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

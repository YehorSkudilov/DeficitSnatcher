using DeficitSnatcher.Api.Buying;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Controllers;

public sealed record SiteInput(
    int ProductId,
    string Name,
    string Url,
    SiteKind Kind,
    bool Enabled,
    int? IntervalSeconds,
    decimal ShippingCost,
    string? PriceSelector,
    string? TitleSelector,
    string? InStockSelector,
    string? OutOfStockText,
    string? VariantId,
    bool AutoBuy,
    List<CheckoutStep>? CheckoutSteps,
    /// <summary>null: keep the saved cookies; "": remove them.</summary>
    string? Cookies);

public sealed record SiteDto(
    int Id,
    int ProductId,
    string ProductName,
    string Name,
    string Url,
    SiteKind Kind,
    bool Enabled,
    int? IntervalSeconds,
    decimal ShippingCost,
    string? PriceSelector,
    string? TitleSelector,
    string? InStockSelector,
    string? OutOfStockText,
    string? VariantId,
    bool AutoBuy,
    List<CheckoutStep> CheckoutSteps,
    bool HasCookies,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastChangedAt,
    decimal? LastPrice,
    bool? LastInStock,
    string? LastTitle,
    string? LastError,
    int ConsecutiveErrors,
    bool Match,
    string Verdict,
    DateTimeOffset? NextCheckAt,
    bool Checking);

public sealed record ProbeResponse(PageResult? Result, BuyRules.Verdict? Verdict, string? Error, string? BuyMessage);
public sealed record BuyRequest(bool DryRun);
public sealed record HistoryPoint(DateTimeOffset At, decimal? Price, bool InStock);

[ApiController]
[Route("api/sites")]
public sealed class SitesController(AppDbContext db, SecretBox box, Tracker tracker, CheckService checks, Buyer buyer) : ControllerBase
{
    [HttpGet]
    public async Task<List<SiteDto>> List([FromQuery] int? productId, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Include(s => s.Product)
            .Where(s => productId == null || s.ProductId == productId)
            .OrderBy(s => s.Product!.Name).ThenBy(s => s.Name).ToListAsync(ct);
        return sites.Select(ToDto).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SiteDto>> Get(int id, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking().Include(s => s.Product).FirstOrDefaultAsync(s => s.Id == id, ct);
        return site == null ? NotFound() : ToDto(site);
    }

    [HttpPost]
    public async Task<ActionResult<SiteDto>> Create(SiteInput input, CancellationToken ct)
    {
        var site = new Site { CreatedAt = DateTimeOffset.UtcNow };
        if (await ApplyAsync(site, input, ct) is { } problem)
            return problem;
        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
        await db.Entry(site).Reference(s => s.Product).LoadAsync(ct);
        tracker.CheckSoon(site.Id);
        return ToDto(site);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<SiteDto>> Update(int id, SiteInput input, CancellationToken ct)
    {
        var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (site == null)
            return NotFound();
        var urlChanged = site.Url != input.Url?.Trim() || site.ProductId != input.ProductId;
        if (await ApplyAsync(site, input, ct) is { } problem)
            return problem;
        if (urlChanged)
        {
            // Another page: what was seen on the old one says nothing about it.
            site.LastPrice = null;
            site.LastInStock = null;
            site.LastTitle = null;
            site.LastVariantId = null;
        }
        await db.SaveChangesAsync(ct);
        await db.Entry(site).Reference(s => s.Product).LoadAsync(ct);
        tracker.CheckSoon(site.Id);
        return ToDto(site);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) =>
        await db.Sites.Where(s => s.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    /// <summary>Checks it now (and buys, if it's a match and auto-buy is on: the same as a scheduled check).</summary>
    [HttpPost("{id:int}/check")]
    public async Task<ProbeResponse> CheckNow(int id, CancellationToken ct)
    {
        var o = await checks.CheckAsync(id, ct, manual: true);
        return new(o.Result, o.Verdict, o.Error, o.BuyMessage);
    }

    /// <summary>Checks the page as set on the form, saving nothing.</summary>
    [HttpPost("test")]
    public async Task<ActionResult<ProbeResponse>> Test(SiteInput input, [FromQuery] int? id, CancellationToken ct)
    {
        var site = new Site();
        var saved = id == null ? null : await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (await ApplyAsync(site, input, ct, keepCookiesFrom: saved) is { } problem)
            return problem;
        var product = await db.Products.AsNoTracking().FirstAsync(p => p.Id == site.ProductId, ct);
        var cookies = site.CookiesEncrypted == null ? null : box.Decrypt(site.CookiesEncrypted);
        var o = await checks.ProbeAsync(site, product, cookies, ct);
        return new ProbeResponse(o.Result, o.Verdict, o.Error, null);
    }

    /// <summary>Runs the checkout now at the last price seen: a dry run to test the steps, or for real.</summary>
    [HttpPost("{id:int}/buy")]
    public async Task<ActionResult<Buyer.Outcome>> Buy(int id, BuyRequest request, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (site == null)
            return NotFound();
        if (!request.DryRun && site.LastPrice == null)
            return Problem("No price seen yet: check the site first.", statusCode: StatusCodes.Status400BadRequest);
        var price = (site.LastPrice ?? 0) + site.ShippingCost;
        var trigger = $"Started by hand from the web ({(request.DryRun ? "Dry-run checkout" : "Buy now")}) at {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"
            + $"\nLast seen: {site.LastTitle ?? "—"} · price {site.LastPrice:0.00}{(site.ShippingCost > 0 ? $" + {site.ShippingCost:0.00} shipping" : "")}"
            + $" · {(site.LastInStock == true ? "in stock" : site.LastInStock == false ? "out of stock" : "stock unknown")}, checked {site.LastCheckedAt:yyyy-MM-dd HH:mm:ss} UTC";
        return await buyer.BuyAsync(id, price, manual: true, forceDryRun: request.DryRun, trigger, ct);
    }

    [HttpGet("{id:int}/history")]
    public Task<List<HistoryPoint>> History(int id, [FromQuery] int days = 14, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        return db.PricePoints.AsNoTracking().Where(p => p.SiteId == id && p.At >= since).OrderBy(p => p.At)
            .Select(p => new HistoryPoint(p.At, p.Price, p.InStock)).ToListAsync(ct);
    }

    private async Task<ActionResult?> ApplyAsync(Site site, SiteInput i, CancellationToken ct, Site? keepCookiesFrom = null)
    {
        string? error = null;
        var url = i.Url?.Trim() ?? "";
        var steps = i.CheckoutSteps ?? [];
        if (string.IsNullOrWhiteSpace(i.Name))
            error = "Give it a name.";
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            error = "The URL must be a full http(s) address.";
        else if (!await db.Products.AnyAsync(p => p.Id == i.ProductId, ct))
            error = "Pick the product it sells.";
        else if (i.IntervalSeconds is < 5)
            error = "Check at most every 5 seconds.";
        else if (i.ShippingCost < 0)
            error = "Shipping can't be negative.";
        else if (CheckoutStep.Validate(steps) is { } stepError)
            error = stepError;
        else if (i.Cookies is { Length: > 0 } && SiteCookies.Validate(i.Cookies, url) is { } cookieError)
            error = cookieError;
        if (error != null)
            return Problem(error, statusCode: StatusCodes.Status400BadRequest);

        site.ProductId = i.ProductId;
        site.Name = i.Name.Trim();
        site.Url = url;
        site.Kind = i.Kind;
        site.Enabled = i.Enabled;
        site.IntervalSeconds = i.IntervalSeconds;
        site.ShippingCost = i.ShippingCost;
        site.PriceSelector = Blank(i.PriceSelector);
        site.TitleSelector = Blank(i.TitleSelector);
        site.InStockSelector = Blank(i.InStockSelector);
        site.OutOfStockText = Blank(i.OutOfStockText);
        site.VariantId = Blank(i.VariantId);
        site.AutoBuy = i.AutoBuy;
        site.CheckoutSteps = CheckoutStep.Serialize(steps);
        if (i.Cookies != null)
            site.CookiesEncrypted = i.Cookies.Trim().Length == 0 ? null : box.Encrypt(i.Cookies.Trim());
        else if (keepCookiesFrom != null)
            site.CookiesEncrypted = keepCookiesFrom.CookiesEncrypted;
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private SiteDto ToDto(Site s)
    {
        var verdict = s.Product == null
            ? new BuyRules.Verdict(false, null, "")
            : BuyRules.Evaluate(s.Product, s, new PageResult { Price = s.LastPrice, InStock = s.LastInStock, Title = s.LastTitle });
        if (s.LastCheckedAt == null)
            verdict = verdict with { Reason = "Not checked yet" };
        return new SiteDto(s.Id, s.ProductId, s.Product?.Name ?? "", s.Name, s.Url, s.Kind, s.Enabled, s.IntervalSeconds, s.ShippingCost,
            s.PriceSelector, s.TitleSelector, s.InStockSelector, s.OutOfStockText, s.VariantId, s.AutoBuy, CheckoutStep.Parse(s.CheckoutSteps),
            s.CookiesEncrypted != null, s.LastCheckedAt, s.LastChangedAt, s.LastPrice, s.LastInStock, s.LastTitle, s.LastError,
            s.ConsecutiveErrors, verdict.Match, verdict.Reason, tracker.NextCheck(s.Id), tracker.IsChecking(s.Id));
    }
}

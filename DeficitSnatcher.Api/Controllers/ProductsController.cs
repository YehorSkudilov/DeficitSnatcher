using DeficitSnatcher.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Controllers;

public sealed record ProductInput(
    string Name,
    bool Enabled,
    decimal MinPrice,
    decimal MaxPrice,
    int MaxQuantity,
    int QuantityPerOrder,
    string? RequiredKeywords,
    string? ExcludedKeywords,
    bool AutoBuyEnabled = false);

public sealed record ProductCheckingRequest(bool Paused, int? CooldownMinutes = null);
public sealed record ProductAutoBuyRequest(bool Enabled);

public sealed record ProductDto(
    int Id,
    string Name,
    bool Enabled,
    decimal MinPrice,
    decimal MaxPrice,
    int MaxQuantity,
    int QuantityPerOrder,
    string RequiredKeywords,
    string ExcludedKeywords,
    int Bought,
    int Sites,
    DateTimeOffset CreatedAt,
    bool CheckingPaused,
    DateTimeOffset? CheckingResumeAt,
    bool AutoBuyEnabled);

[ApiController]
[Route("api/products")]
public sealed class ProductsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<ProductDto>> List(CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new { p, Sites = p.Sites.Count }).ToListAsync(ct);
        var bought = await Bought(ct);
        return products.Select(x => ToDto(x.p, bought.GetValueOrDefault(x.p.Id), x.Sites)).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ProductDto>> Create(ProductInput input, CancellationToken ct)
    {
        if (Check(input) is { } problem)
            return problem;
        var product = new Product { CreatedAt = DateTimeOffset.UtcNow };
        Apply(product, input);
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return ToDto(product, 0, 0);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ProductDto>> Update(int id, ProductInput input, CancellationToken ct)
    {
        if (Check(input) is { } problem)
            return problem;
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product == null)
            return NotFound();
        Apply(product, input);
        await db.SaveChangesAsync(ct);
        var sites = await db.Sites.CountAsync(s => s.ProductId == id, ct);
        return ToDto(product, (await Bought(ct)).GetValueOrDefault(id), sites);
    }

    /// <summary>
    /// Deletes the product with its pages and everything they kept. Done in steps (checks stopped first, then the scans and
    /// their screenshots in batches): one cascading delete of thousands of scans ran past the database's time limit while
    /// checks kept adding to them, and failed.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product == null)
            return NotFound();
        // No new checks or buys while it goes.
        product.Enabled = false;
        product.AutoBuyEnabled = false;
        product.CheckingPaused = true;
        product.CheckingResumeAt = null;
        await db.SaveChangesAsync(ct);

        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var siteIds = await db.Sites.Where(s => s.ProductId == id).Select(s => s.Id).ToListAsync(ct);
        while (true)
        {
            var batch = await db.Scans.Where(s => siteIds.Contains(s.SiteId)).OrderBy(s => s.Id).Select(s => s.Id).Take(500).ToListAsync(ct);
            if (batch.Count == 0)
                break;
            await db.ScanSnapshots.Where(x => batch.Contains(x.ScanId)).ExecuteDeleteAsync(ct);
            await db.Scans.Where(s => batch.Contains(s.Id)).ExecuteDeleteAsync(ct);
        }
        var purchaseIds = await db.Purchases.Where(p => p.ProductId == id || siteIds.Contains(p.SiteId)).Select(p => p.Id).ToListAsync(ct);
        await db.PurchaseShots.Where(x => purchaseIds.Contains(x.PurchaseId)).ExecuteDeleteAsync(ct);
        await db.Purchases.Where(p => purchaseIds.Contains(p.Id)).ExecuteDeleteAsync(ct);
        await db.PricePoints.Where(x => siteIds.Contains(x.SiteId)).ExecuteDeleteAsync(ct);
        await db.AiCalls.Where(x => x.SiteId != null && siteIds.Contains(x.SiteId.Value)).ExecuteDeleteAsync(ct);
        await db.BrowserSessions.Where(x => siteIds.Contains(x.SiteId)).ExecuteDeleteAsync(ct);
        await db.RestockInsights.Where(x => x.ProductId == id).ExecuteDeleteAsync(ct);
        await db.Sites.Where(s => s.ProductId == id).ExecuteDeleteAsync(ct);
        await db.Products.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:int}/checking")]
    public async Task<ActionResult<ProductDto>> SetChecking(int id, ProductCheckingRequest request, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product == null)
            return NotFound();
        product.CheckingPaused = request.Paused;
        product.CheckingResumeAt = request.Paused && request.CooldownMinutes is > 0
            ? DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(request.CooldownMinutes.Value, 1, 10080))
            : null;
        await db.SaveChangesAsync(ct);
        var sites = await db.Sites.CountAsync(s => s.ProductId == id, ct);
        return ToDto(product, (await Bought(ct)).GetValueOrDefault(id), sites);
    }

    [HttpPost("{id:int}/auto-buy")]
    public async Task<ActionResult<ProductDto>> SetAutoBuy(int id, ProductAutoBuyRequest request, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product == null)
            return NotFound();
        product.AutoBuyEnabled = request.Enabled;
        await db.SaveChangesAsync(ct);
        var sites = await db.Sites.CountAsync(s => s.ProductId == id, ct);
        return ToDto(product, (await Bought(ct)).GetValueOrDefault(id), sites);
    }

    private Task<Dictionary<int, int>> Bought(CancellationToken ct) => db.Purchases
        .Where(p => p.Status == PurchaseStatus.Succeeded && !p.DryRun)
        .GroupBy(p => p.ProductId)
        .Select(g => new { g.Key, Units = g.Sum(p => p.Quantity) })
        .ToDictionaryAsync(g => g.Key, g => g.Units, ct);

    private ActionResult? Check(ProductInput i)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(i.Name))
            error = "Give it a name.";
        else if (i.Name.Trim().Length > 200)
            error = "The name is too long (200 characters at most).";
        else if ((i.RequiredKeywords ?? "").Trim().Length > 1000 || (i.ExcludedKeywords ?? "").Trim().Length > 1000)
            error = $"The keywords are too long: {Math.Max((i.RequiredKeywords ?? "").Trim().Length, (i.ExcludedKeywords ?? "").Trim().Length)} characters, 1,000 at most.";
        else if (i.MinPrice < 0 || i.MaxPrice <= 0)
            error = "Prices must be positive.";
        else if (i.MinPrice > i.MaxPrice)
            error = "The minimum price is over the maximum.";
        else if (i.MaxQuantity < 0 || i.QuantityPerOrder < 1)
            error = "Quantities must be at least 1 per order (and 0 or more in all).";
        else if (i.QuantityPerOrder > Math.Max(i.MaxQuantity, 1))
            error = "Units per order can't be more than units in all.";
        return error == null ? null : Problem(error, statusCode: StatusCodes.Status400BadRequest);
    }

    private static void Apply(Product p, ProductInput i)
    {
        p.Name = i.Name.Trim();
        p.Enabled = i.Enabled;
        p.MinPrice = i.MinPrice;
        p.MaxPrice = i.MaxPrice;
        p.MaxQuantity = i.MaxQuantity;
        p.QuantityPerOrder = i.QuantityPerOrder;
        p.RequiredKeywords = (i.RequiredKeywords ?? "").Trim();
        p.ExcludedKeywords = (i.ExcludedKeywords ?? "").Trim();
        p.AutoBuyEnabled = i.AutoBuyEnabled;
    }

    private static ProductDto ToDto(Product p, int bought, int sites) => new(p.Id, p.Name, p.Enabled, p.MinPrice, p.MaxPrice,
        p.MaxQuantity, p.QuantityPerOrder, p.RequiredKeywords, p.ExcludedKeywords, bought, sites, p.CreatedAt,
        p.CheckingPaused, p.CheckingResumeAt, p.AutoBuyEnabled);
}

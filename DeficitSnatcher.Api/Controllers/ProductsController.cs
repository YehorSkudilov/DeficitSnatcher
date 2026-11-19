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
    string? ExcludedKeywords);

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
    DateTimeOffset CreatedAt);

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

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await db.Purchases.Where(p => p.ProductId == id).ExecuteDeleteAsync(ct);
        return await db.Products.Where(p => p.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();
    }

    private Task<Dictionary<int, int>> Bought(CancellationToken ct) => db.Purchases
        .Where(p => p.Status == PurchaseStatus.Succeeded)
        .GroupBy(p => p.ProductId)
        .Select(g => new { g.Key, Units = g.Sum(p => p.Quantity) })
        .ToDictionaryAsync(g => g.Key, g => g.Units, ct);

    private ActionResult? Check(ProductInput i)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(i.Name))
            error = "Give it a name.";
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
    }

    private static ProductDto ToDto(Product p, int bought, int sites) => new(p.Id, p.Name, p.Enabled, p.MinPrice, p.MaxPrice,
        p.MaxQuantity, p.QuantityPerOrder, p.RequiredKeywords, p.ExcludedKeywords, bought, sites, p.CreatedAt);
}

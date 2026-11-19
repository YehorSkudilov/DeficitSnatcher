using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Controllers;

public sealed record PurchaseDto(
    int Id,
    int SiteId,
    string SiteName,
    int ProductId,
    string ProductName,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    decimal Price,
    int Quantity,
    PurchaseStatus Status,
    string Message,
    bool HasScreenshot);

public sealed record PurchaseDetailDto(PurchaseDto Purchase, string Log);

public sealed record EventDto(long Id, DateTimeOffset At, EventLevel Level, int? SiteId, string? SiteName, string Message);

public sealed record DashboardDto(
    bool AutoBuyArmed,
    bool DryRun,
    bool TrackingEnabled,
    string Currency,
    decimal TotalBudget,
    decimal Spent,
    int Sites,
    int SitesEnabled,
    int SitesInStock,
    int SitesFailing,
    int ChecksLastHour,
    List<EventDto> RecentEvents,
    List<PurchaseDto> RecentPurchases);

[ApiController]
[Route("api")]
public sealed class ActivityController(AppDbContext db, SettingsStore settingsStore) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<DashboardDto> Dashboard(CancellationToken ct)
    {
        var s = await settingsStore.GetAsync(ct);
        var sites = await db.Sites.AsNoTracking()
            .Select(x => new { x.Enabled, ProductEnabled = x.Product!.Enabled, x.LastInStock, x.ConsecutiveErrors, x.LastCheckedAt })
            .ToListAsync(ct);
        var spent = (await db.Purchases.Where(p => p.Status == PurchaseStatus.Succeeded).Select(p => new { p.Price, p.Quantity }).ToListAsync(ct))
            .Sum(p => p.Price * p.Quantity);
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        return new DashboardDto(
            s.AutoBuyArmed,
            s.DryRun,
            s.TrackingEnabled,
            s.Currency,
            s.TotalBudget,
            spent,
            sites.Count,
            sites.Count(x => x.Enabled && x.ProductEnabled),
            sites.Count(x => x.Enabled && x.LastInStock == true),
            sites.Count(x => x.Enabled && x.ConsecutiveErrors > 0),
            sites.Count(x => x.LastCheckedAt >= hourAgo),
            await Events(null, 15, ct),
            await Purchases(8, ct));
    }

    [HttpGet("events")]
    public Task<List<EventDto>> ListEvents([FromQuery] long? before, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Events(before, Math.Clamp(limit, 1, 500), ct);

    [HttpDelete("events")]
    public async Task<IActionResult> ClearEvents(CancellationToken ct)
    {
        await db.Events.ExecuteDeleteAsync(ct);
        return NoContent();
    }

    [HttpGet("purchases")]
    public Task<List<PurchaseDto>> ListPurchases([FromQuery] int limit = 200, CancellationToken ct = default) =>
        Purchases(Math.Clamp(limit, 1, 1000), ct);

    [HttpGet("purchases/{id:int}")]
    public async Task<ActionResult<PurchaseDetailDto>> GetPurchase(int id, CancellationToken ct)
    {
        var row = await PurchaseQuery(db.Purchases.Where(p => p.Id == id)).FirstOrDefaultAsync(ct);
        return row == null ? NotFound() : new PurchaseDetailDto(row.Dto, row.Log);
    }

    [HttpGet("purchases/{id:int}/screenshot")]
    public async Task<IActionResult> Screenshot(int id, CancellationToken ct)
    {
        var png = await db.Purchases.Where(p => p.Id == id).Select(p => p.Screenshot).FirstOrDefaultAsync(ct);
        return png == null ? NotFound() : File(png, "image/png");
    }

    /// <summary>Marks a bought order as not bought (cancelled or refunded), so it no longer counts against the limits.</summary>
    [HttpPost("purchases/{id:int}/void")]
    public async Task<IActionResult> Void(int id, CancellationToken ct)
    {
        var p = await db.Purchases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null)
            return NotFound();
        if (p.Status is PurchaseStatus.Succeeded or PurchaseStatus.Running)
        {
            p.Status = PurchaseStatus.Failed;
            p.Message = "Marked as not bought. " + p.Message;
            p.FinishedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    private sealed record PurchaseRow(PurchaseDto Dto, string Log);

    private IQueryable<PurchaseRow> PurchaseQuery(IQueryable<Purchase> source) =>
        from p in source.AsNoTracking()
        join s in db.Sites on p.SiteId equals s.Id
        join pr in db.Products on p.ProductId equals pr.Id
        select new PurchaseRow(
            new PurchaseDto(p.Id, p.SiteId, s.Name, p.ProductId, pr.Name, p.StartedAt, p.FinishedAt, p.Price, p.Quantity, p.Status, p.Message,
                p.Screenshot != null),
            p.Log);

    private async Task<List<PurchaseDto>> Purchases(int limit, CancellationToken ct) =>
        (await PurchaseQuery(db.Purchases.OrderByDescending(p => p.StartedAt).Take(limit)).ToListAsync(ct))
        .Select(x => x.Dto).OrderByDescending(p => p.StartedAt).ToList();

    private Task<List<EventDto>> Events(long? before, int limit, CancellationToken ct) =>
        (from e in db.Events.AsNoTracking()
         where before == null || e.Id < before
         join s in db.Sites on e.SiteId equals s.Id into sj
         from s in sj.DefaultIfEmpty()
         orderby e.Id descending
         select new EventDto(e.Id, e.At, e.Level, e.SiteId, s == null ? null : s.Name, e.Message))
        .Take(limit).ToListAsync(ct);
}

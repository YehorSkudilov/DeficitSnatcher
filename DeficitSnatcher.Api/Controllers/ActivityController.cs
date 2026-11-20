using System.Text;
using System.Text.Json;
using DeficitSnatcher.Api.Buying;
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
    bool DryRun,
    bool Manual,
    int Shots,
    bool AiUsed,
    string? Worker = null);

public sealed record ShotDto(long Id, int? StepIndex, string Label, DateTimeOffset At);

public sealed record ScanRef(long Id, DateTimeOffset At);

public sealed record PurchaseDetailDto(
    PurchaseDto Purchase,
    string SiteUrl,
    string Context,
    List<Buyer.StepRecord> Steps,
    string Log,
    string? Error,
    string BrowserLog,
    string? FinalUrl,
    bool HasHtml,
    List<ShotDto> Shots,
    ScanRef? Scan);

public sealed record EventDto(long Id, DateTimeOffset At, EventLevel Level, int? SiteId, string? SiteName, string Message);

public sealed record DashboardDto(
    bool AlertsOnly,
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
public sealed class ActivityController(AppDbContext db, SettingsStore settingsStore, SecretBox box) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<DashboardDto> Dashboard(CancellationToken ct)
    {
        var s = await settingsStore.GetAsync(ct);
        var sites = await db.Sites.AsNoTracking()
            .Select(x => new { x.Enabled, ProductEnabled = x.Product!.Enabled, x.LastInStock, x.ConsecutiveErrors })
            .ToListAsync(ct);
        var spent = (await db.Purchases.Where(p => p.Status == PurchaseStatus.Succeeded && !p.DryRun).Select(p => new { p.Price, p.Quantity }).ToListAsync(ct))
            .Sum(p => p.Price * p.Quantity);
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        return new DashboardDto(
            s.AlertsOnly,
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
            await db.Scans.CountAsync(x => x.At >= hourAgo, ct),
            await Events(new EventFilter { Limit = 15 }, ct),
            await Purchases(new PurchaseFilter { Limit = 8 }, ct));
    }

    // ---- Activity log ----

    public sealed class EventFilter
    {
        public long? Before { get; set; }
        public int Limit { get; set; } = 100;
        public EventLevel? Level { get; set; }
        public int? SiteId { get; set; }
        public int? ProductId { get; set; }
        public string? Search { get; set; }
        public DateTimeOffset? From { get; set; }
        public DateTimeOffset? To { get; set; }
    }

    [HttpGet("events")]
    public Task<List<EventDto>> ListEvents([FromQuery] EventFilter filter, CancellationToken ct) => Events(filter, ct);

    [HttpDelete("events")]
    public async Task<IActionResult> ClearEvents(CancellationToken ct)
    {
        await db.Events.ExecuteDeleteAsync(ct);
        return NoContent();
    }

    private Task<List<EventDto>> Events(EventFilter f, CancellationToken ct)
    {
        var q = db.Events.AsNoTracking().AsQueryable();
        if (f.Before != null)
            q = q.Where(e => e.Id < f.Before);
        if (f.Level != null)
            q = q.Where(e => e.Level == f.Level);
        if (f.SiteId != null)
            q = q.Where(e => e.SiteId == f.SiteId);
        if (f.ProductId != null)
            q = q.Where(e => db.Sites.Any(s => s.Id == e.SiteId && s.ProductId == f.ProductId));
        if (f.From != null)
            q = q.Where(e => e.At >= f.From);
        if (f.To != null)
            q = q.Where(e => e.At < f.To);
        if (!string.IsNullOrWhiteSpace(f.Search))
            q = q.Where(e => EF.Functions.ILike(e.Message, "%" + f.Search.Trim() + "%"));
        return (from e in q
                join s in db.Sites on e.SiteId equals s.Id into sj
                from s in sj.DefaultIfEmpty()
                orderby e.Id descending
                select new EventDto(e.Id, e.At, e.Level, e.SiteId, s == null ? null : s.Name, e.Message))
            .Take(Math.Clamp(f.Limit, 1, 500)).ToListAsync(ct);
    }

    // ---- Checkouts ----

    public sealed class PurchaseFilter
    {
        public int Limit { get; set; } = 200;
        public int? Before { get; set; }
        public PurchaseStatus? Status { get; set; }
        public int? SiteId { get; set; }
        public int? ProductId { get; set; }
        /// <summary>true: only dry runs; false: only real ones.</summary>
        public bool? DryRun { get; set; }
        public DateTimeOffset? From { get; set; }
        public DateTimeOffset? To { get; set; }
    }

    [HttpGet("purchases")]
    public Task<List<PurchaseDto>> ListPurchases([FromQuery] PurchaseFilter filter, CancellationToken ct) => Purchases(filter, ct);

    [HttpGet("purchases/{id:int}")]
    public async Task<ActionResult<PurchaseDetailDto>> GetPurchase(int id, CancellationToken ct)
    {
        var row = (await Named(await PurchaseQuery(db.Purchases.Where(p => p.Id == id)).ToListAsync(ct), ct)).FirstOrDefault();
        if (row == null)
            return NotFound();
        var p = await db.Purchases.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Context, x.Steps, x.Log, x.Error, x.BrowserLog, x.FinalUrl, HasHtml = x.PageHtml != null })
            .FirstAsync(ct);
        var url = await db.Sites.Where(s => s.Id == row.SiteId).Select(s => s.Url).FirstAsync(ct);
        var shots = await db.PurchaseShots.AsNoTracking().Where(s => s.PurchaseId == id).OrderBy(s => s.Id)
            .Select(s => new ShotDto(s.Id, s.StepIndex, s.Label, s.At)).ToListAsync(ct);
        var scan = await db.Scans.AsNoTracking().Where(s => s.PurchaseId == id).Select(s => new ScanRef(s.Id, s.At)).FirstOrDefaultAsync(ct);
        var steps = JsonSerializer.Deserialize<List<Buyer.StepRecord>>(p.Steps, Buyer.JsonOptions) ?? [];
        return new PurchaseDetailDto(row, url, p.Context, steps, p.Log, p.Error, p.BrowserLog, p.FinalUrl, p.HasHtml, shots, scan);
    }

    [HttpGet("purchases/{id:int}/shots/{shotId:long}")]
    public async Task<IActionResult> Shot(int id, long shotId, CancellationToken ct)
    {
        var shot = await db.PurchaseShots.AsNoTracking().Where(s => s.Id == shotId && s.PurchaseId == id)
            .Select(s => new { s.Data, s.ContentType }).FirstOrDefaultAsync(ct);
        if (shot == null)
            return NotFound();
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(box.DecryptBytes(shot.Data), shot.ContentType);
    }

    /// <summary>The page's HTML where the checkout ended, as a download (never rendered here).</summary>
    [HttpGet("purchases/{id:int}/html")]
    public async Task<IActionResult> Html(int id, CancellationToken ct)
    {
        var html = await db.Purchases.Where(p => p.Id == id).Select(p => p.PageHtml).FirstOrDefaultAsync(ct);
        if (html == null)
            return NotFound();
        return File(Encoding.UTF8.GetBytes(box.Decrypt(html)), "application/octet-stream", $"checkout-{id}.html");
    }

    /// <summary>Marks a bought order as not bought (cancelled or refunded), so it no longer counts against the limits.</summary>
    [HttpPost("purchases/{id:int}/void")]
    public async Task<IActionResult> Void(int id, CancellationToken ct)
    {
        var p = await db.Purchases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null)
            return NotFound();
        if (p.Status is PurchaseStatus.Succeeded or PurchaseStatus.Running or PurchaseStatus.Interrupted)
        {
            p.Status = PurchaseStatus.Failed;
            p.Message = "Marked as not bought. " + p.Message;
            p.FinishedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpDelete("purchases/{id:int}")]
    public async Task<IActionResult> DeletePurchase(int id, CancellationToken ct)
    {
        var status = await db.Purchases.Where(p => p.Id == id).Select(p => (PurchaseStatus?)p.Status).FirstOrDefaultAsync(ct);
        if (status == null)
            return NotFound();
        if (status is PurchaseStatus.Succeeded or PurchaseStatus.Running or PurchaseStatus.Interrupted)
            return Problem("A bought, running or interrupted checkout counts against the limits: mark it as not bought first.", statusCode: 400);
        await db.Purchases.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    private IQueryable<PurchaseDto> PurchaseQuery(IQueryable<Purchase> source) =>
        from p in source.AsNoTracking()
        join s in db.Sites on p.SiteId equals s.Id
        join pr in db.Products on p.ProductId equals pr.Id
        select new PurchaseDto(p.Id, p.SiteId, s.Name, p.ProductId, pr.Name, p.StartedAt, p.FinishedAt, p.Price, p.Quantity, p.Status, p.Message,
            p.DryRun, p.Manual, db.PurchaseShots.Count(x => x.PurchaseId == p.Id), p.AiUsed, p.Worker);

    private async Task<List<PurchaseDto>> Named(List<PurchaseDto> rows, CancellationToken ct)
    {
        var names = await db.RunnerKeys.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        return rows.Select(p => p with { Worker = ScansController.WorkerName(p.Worker, names) }).ToList();
    }

    private async Task<List<PurchaseDto>> Purchases(PurchaseFilter f, CancellationToken ct)
    {
        var q = db.Purchases.AsQueryable();
        if (f.Before != null)
            q = q.Where(p => p.Id < f.Before);
        if (f.Status != null)
            q = q.Where(p => p.Status == f.Status);
        if (f.SiteId != null)
            q = q.Where(p => p.SiteId == f.SiteId);
        if (f.ProductId != null)
            q = q.Where(p => p.ProductId == f.ProductId);
        if (f.DryRun != null)
            q = q.Where(p => p.DryRun == f.DryRun);
        if (f.From != null)
            q = q.Where(p => p.StartedAt >= f.From);
        if (f.To != null)
            q = q.Where(p => p.StartedAt < f.To);
        q = q.OrderByDescending(p => p.Id).Take(Math.Clamp(f.Limit, 1, 1000));
        return (await Named(await PurchaseQuery(q).ToListAsync(ct), ct)).OrderByDescending(p => p.Id).ToList();
    }
}

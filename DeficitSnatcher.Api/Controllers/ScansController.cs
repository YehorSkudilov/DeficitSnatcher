using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Controllers;

public sealed record ScanDto(
    long Id,
    int SiteId,
    string SiteName,
    int ProductId,
    string ProductName,
    DateTimeOffset At,
    int DurationMs,
    ScanOutcome Outcome,
    decimal? Price,
    decimal? TotalPrice,
    string? Title,
    bool Match,
    string Verdict,
    string? Error,
    string? Notes,
    string? Action,
    int? PurchaseId,
    bool Manual,
    bool HasSnapshot);

public sealed record SnapshotDto(string Reason, bool HasScreenshot, bool HasSource, string? SourceType, string? Error);

/// <summary>How many scans the filter matches, by outcome (the filter chips' numbers; the outcome filter itself left out).</summary>
public sealed record ScanCounts(int All, int InStock, int OutOfStock, int Unknown, int Error, int Match, int Bought);

public sealed record ScansPage(List<ScanDto> Scans, ScanCounts Counts);

/// <summary>Every check of every site, whatever it found: the Scans page.</summary>
[ApiController]
[Route("api/scans")]
public sealed class ScansController(AppDbContext db, SecretBox box, ScanFollowUp followUp) : ControllerBase
{
    public sealed class ScanFilter
    {
        public int? SiteId { get; set; }
        public int? ProductId { get; set; }
        /// <summary>InStock, OutOfStock, Unknown, Error, Match (in range), Bought (a checkout ran).</summary>
        public string? Outcome { get; set; }
        public DateTimeOffset? From { get; set; }
        public DateTimeOffset? To { get; set; }
        public decimal? MinPrice { get; set; }
        public decimal? MaxPrice { get; set; }
        public bool? Manual { get; set; }
        public string? Search { get; set; }
        /// <summary>Paging: scans older than this id.</summary>
        public long? Before { get; set; }
        public int Limit { get; set; } = 100;
    }

    [HttpGet]
    public async Task<ScansPage> List([FromQuery] ScanFilter f, CancellationToken ct)
    {
        var q = db.Scans.AsNoTracking().AsQueryable();
        if (f.SiteId != null)
            q = q.Where(s => s.SiteId == f.SiteId);
        if (f.ProductId != null)
            q = q.Where(s => s.ProductId == f.ProductId);
        if (f.From != null)
            q = q.Where(s => s.At >= f.From);
        if (f.To != null)
            q = q.Where(s => s.At < f.To);
        if (f.MinPrice != null)
            q = q.Where(s => s.TotalPrice >= f.MinPrice);
        if (f.MaxPrice != null)
            q = q.Where(s => s.TotalPrice <= f.MaxPrice);
        if (f.Manual != null)
            q = q.Where(s => s.Manual == f.Manual);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var like = "%" + f.Search.Trim() + "%";
            q = q.Where(s => EF.Functions.ILike(s.Title ?? "", like) || EF.Functions.ILike(s.Verdict, like)
                || EF.Functions.ILike(s.Error ?? "", like) || EF.Functions.ILike(s.Action ?? "", like));
        }

        var byOutcome = await q.GroupBy(s => s.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(ScanOutcome o) => byOutcome.FirstOrDefault(x => x.Key == o)?.Count ?? 0;
        var counts = new ScanCounts(byOutcome.Sum(x => x.Count), Of(ScanOutcome.InStock), Of(ScanOutcome.OutOfStock), Of(ScanOutcome.Unknown),
            Of(ScanOutcome.Error), await q.CountAsync(s => s.Match, ct), await q.CountAsync(s => s.PurchaseId != null, ct));

        q = f.Outcome switch
        {
            "Match" => q.Where(s => s.Match),
            "Bought" => q.Where(s => s.PurchaseId != null),
            { Length: > 0 } o when Enum.TryParse<ScanOutcome>(o, out var outcome) => q.Where(s => s.Outcome == outcome),
            _ => q,
        };
        if (f.Before != null)
            q = q.Where(s => s.Id < f.Before);

        var scans = await (from s in q
                           join site in db.Sites on s.SiteId equals site.Id
                           join p in db.Products on s.ProductId equals p.Id
                           orderby s.Id descending
                           select new ScanDto(s.Id, s.SiteId, site.Name, s.ProductId, p.Name, s.At, s.DurationMs, s.Outcome, s.Price, s.TotalPrice,
                               s.Title, s.Match, s.Verdict, s.Error, s.Notes, s.Action, s.PurchaseId, s.Manual,
                               db.ScanSnapshots.Any(x => x.ScanId == s.Id)))
            .Take(Math.Clamp(f.Limit, 1, 500)).ToListAsync(ct);
        return new ScansPage(scans, counts);
    }

    [HttpGet("{id:long}/snapshot")]
    public async Task<ActionResult<SnapshotDto>> Snapshot(long id, CancellationToken ct)
    {
        var s = await db.ScanSnapshots.AsNoTracking().Where(x => x.ScanId == id)
            .Select(x => new SnapshotDto(x.Reason, x.Screenshot != null, x.Source != null, x.SourceType, x.Error)).FirstOrDefaultAsync(ct);
        return s == null ? NotFound() : s;
    }

    [HttpGet("{id:long}/screenshot")]
    public async Task<IActionResult> Screenshot(long id, CancellationToken ct)
    {
        var shot = await db.ScanSnapshots.Where(x => x.ScanId == id).Select(x => x.Screenshot).FirstOrDefaultAsync(ct);
        if (shot == null)
            return NotFound();
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(box.DecryptBytes(shot), "image/jpeg");
    }

    /// <summary>The fetched page as it was, as a download (never rendered here).</summary>
    [HttpGet("{id:long}/source")]
    public async Task<IActionResult> Source(long id, CancellationToken ct)
    {
        var s = await db.ScanSnapshots.Where(x => x.ScanId == id).Select(x => new { x.Source, x.SourceType }).FirstOrDefaultAsync(ct);
        if (s?.Source == null)
            return NotFound();
        return File(followUp.ReadSource(s.Source), "application/octet-stream", $"scan-{id}.{(s.SourceType == "application/json" ? "json" : "html")}");
    }

    [HttpDelete]
    public async Task<IActionResult> Clear([FromQuery] int? siteId, CancellationToken ct)
    {
        await db.Scans.Where(s => siteId == null || s.SiteId == siteId).ExecuteDeleteAsync(ct);
        return NoContent();
    }
}

using DeficitSnatcher.Api.Ai;
using DeficitSnatcher.Api.Buying;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Controllers;

public sealed record AiStatus(bool HasKey, bool Enabled, string Model, int TokensToday, int DailyTokenLimit);
public sealed record AiSetupRequest(string Url, int ProductId, int? SiteId, string? Cookies);
public sealed record AiCallDto(long Id, DateTimeOffset At, string Purpose, int? SiteId, string? SiteName, int? PurchaseId, string Model,
    int InputTokens, int OutputTokens, int DurationMs, bool Ok, string Summary);

/// <summary>The AI (ChatGPT): set a page up from its link, record a checkout, and the log of every AI call.</summary>
[ApiController]
[Route("api/ai")]
public sealed class AiController(AppDbContext db, OpenAiClient ai, PageSetup setup, SettingsStore settingsStore, SecretBox box, Buyer buyer)
    : ControllerBase
{
    [HttpGet("status")]
    public async Task<AiStatus> Status(CancellationToken ct)
    {
        var s = await settingsStore.GetAsync(ct);
        return new AiStatus(ai.HasKey, s.AiEnabled, s.AiModel, await ai.TokensTodayAsync(ct), s.AiDailyTokenLimit);
    }

    /// <summary>Reads the page and suggests its settings (checked before they're returned). Nothing is saved.</summary>
    [HttpPost("setup")]
    public async Task<ActionResult<SetupSuggestion>> Setup(AiSetupRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Problem("Paste the product page's full address first.", statusCode: 400);
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId, ct);
        if (product == null)
            return Problem("Pick the product first.", statusCode: 400);
        var cookiesText = request.Cookies;
        if (string.IsNullOrWhiteSpace(cookiesText) && request.SiteId is { } siteId)
            cookiesText = await db.Sites.Where(s => s.Id == siteId).Select(s => s.CookiesEncrypted).FirstOrDefaultAsync(ct) is { } sealedCookies
                ? box.Decrypt(sealedCookies)
                : null;
        try
        {
            return await setup.SuggestAsync(uri.ToString(), product, SiteCookies.Parse(cookiesText, uri.ToString()), request.SiteId, "setup", ct);
        }
        catch (OpenAiClient.AiException e)
        {
            return Problem(e.Message, statusCode: 400);
        }
        catch (Microsoft.Playwright.PlaywrightException e)
        {
            return Problem("Couldn't open the page in the browser: " + e.Message.Split('\n')[0], statusCode: 502);
        }
    }

    /// <summary>The AI goes through the page's checkout in a dry run and saves the steps it took.</summary>
    [HttpPost("record-checkout/{siteId:int}")]
    public async Task<ActionResult<Buyer.Outcome>> RecordCheckout(int siteId, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site == null)
            return NotFound();
        var trigger = $"Record checkout with AI, started from the site at {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC: the AI goes through the "
            + "checkout as a dry run and the steps it takes are saved.";
        return await buyer.BuyAsync(siteId, (site.LastPrice ?? 0) + site.ShippingCost, manual: true, forceDryRun: true, trigger, ct, aiRecord: true);
    }

    [HttpGet("calls")]
    public Task<List<AiCallDto>> Calls([FromQuery] int? siteId, [FromQuery] int? productId, [FromQuery] long? before, [FromQuery] int limit = 100,
        CancellationToken ct = default) =>
        (from c in db.AiCalls.AsNoTracking()
         join s in db.Sites on c.SiteId equals s.Id into sj
         from s in sj.DefaultIfEmpty()
         where (siteId == null || c.SiteId == siteId) && (productId == null || s.ProductId == productId) && (before == null || c.Id < before)
         orderby c.Id descending
         select new AiCallDto(c.Id, c.At, c.Purpose, c.SiteId, s == null ? null : s.Name, c.PurchaseId, c.Model, c.InputTokens, c.OutputTokens,
             c.DurationMs, c.Ok, c.Summary))
        .Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
}

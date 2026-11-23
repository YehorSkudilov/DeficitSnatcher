using DeficitSnatcher.Api.Ai;
using DeficitSnatcher.Api.Buying;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Controllers;

public sealed record AiStatus(bool HasKey, bool Enabled, string Model, int TokensToday, int DailyTokenLimit);
public sealed record AiSetupRequest(string Url, int ProductId, int? SiteId, string? Cookies);
public sealed record ProductFillRequest(string Name, string? Hint);
public sealed record ProductFill(string Name, decimal? MinPrice, decimal? MaxPrice, string RequiredKeywords, string ExcludedKeywords, string? Note);
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

    private const string ProductFillInstructions = """
        You fill in a product watcher's settings from the product's name, using web search for its current retail price. The watcher
        checks store pages and buys only when the listing's title fits the keywords and its price plus shipping is in the range.
        - name: the product's clear, usual name (e.g. "PS5 Pro"), fixing typos.
        - minPrice: well under any real price for it new (about 60% of the usual retail price, rounded), so accessories, cases,
          parts, "box only" and scam listings that share the name are caught.
        - maxPrice: the most worth paying new: the usual retail price (MSRP) in the given currency, plus about 10% headroom for
          price differences between stores (before tax), rounded up to a round number. Not scalper prices.
        - requiredKeywords: comma-separated groups that every real listing's title contains; within a group "a|b" means either
          spelling (e.g. "PS5 Pro|PlayStation 5 Pro"). Matching ignores case, spaces, hyphens and ®/™. Keep it to what tells this
          exact product apart (model name/number, capacity or size when it matters); don't require words stores often leave out.
        - excludedKeywords: comma-separated words in titles of things that aren't it: other models or editions, accessories
          (case, cover, stand, controller, charger, skin, cable…), used/refurbished/open box/renewed, bundles only if unwanted.
          Never a word that the real product's titles contain.
        - note: one short line: the retail price you found and where, and anything to watch for.
        Answer with JSON only:
        { "name": "…", "minPrice": 0, "maxPrice": 0, "requiredKeywords": "…", "excludedKeywords": "…", "note": "…" }
        """;

    /// <summary>The AI suggests a product's name, price range and title keywords from its name (with web search). Nothing is saved.</summary>
    [HttpPost("product-fill")]
    public async Task<ActionResult<ProductFill>> ProductFill(ProductFillRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Problem("Type the product's name first.", statusCode: 400);
        var settings = await settingsStore.GetAsync(ct);
        var profile = await settingsStore.GetProfileAsync(ct);
        var country = StoreRegion.For(profile.Country, settings.Currency)?.Name ?? "the owner's country";
        var question = $"""
            Product: {name}
            Country: {country}. Currency: {settings.Currency} (all prices in it).
            {(string.IsNullOrWhiteSpace(request.Hint) ? "" : "Also: " + request.Hint.Trim())}
            """;
        JsonElement a;
        try
        {
            a = await ai.SearchWebJsonAsync("product-fill", ProductFillInstructions, question, ct);
        }
        catch (OpenAiClient.AiException e)
        {
            return Problem(e.Message, statusCode: 400);
        }
        static string? Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s ? s : null;
        static decimal? Num(JsonElement e, string key)
        {
            if (!e.TryGetProperty(key, out var v))
                return null;
            decimal? n = null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
                n = d;
            else if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString()?.Trim().TrimStart('$').Replace(",", ""),
                         System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p))
                n = p;
            return n is { } x && x >= 0 ? Math.Round(x, 2) : null;
        }
        var min = Num(a, "minPrice");
        var max = Num(a, "maxPrice");
        if (min is { } lo && max is { } hi && lo > hi)
            (min, max) = (hi, lo);
        return new ProductFill(Str(a, "name") ?? name, min, max, Str(a, "requiredKeywords") ?? "", Str(a, "excludedKeywords") ?? "",
            Str(a, "note"));
    }

    /// <summary>The AI goes through the page's checkout in a dry run and saves the steps it took.</summary>
    [HttpPost("record-checkout/{siteId:int}")]
    public async Task<ActionResult<Buyer.Outcome>> RecordCheckout(int siteId, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site == null)
            return NotFound();
        var settings = await settingsStore.GetAsync(ct);
        var trigger = $"Record checkout with AI, started from the site at {Times.At(DateTimeOffset.UtcNow)}: the AI goes through the "
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

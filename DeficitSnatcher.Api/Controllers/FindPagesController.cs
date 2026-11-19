using System.Text.Json;
using DeficitSnatcher.Api.Ai;
using DeficitSnatcher.Api.Buying;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Controllers;

public sealed record FindRequest(int ProductId, string? Hint);
public sealed record FoundPage(string Store, string Url, string? Title, string? Price, string? Note, bool AlreadyAdded);
public sealed record ProbeManyRequest(int ProductId, List<string> Urls);
/// <summary>reads (price and stock found), blocked (bot protection), unread (no price or stock found), error.</summary>
public sealed record ProbedPage(string Url, string Name, string Status, decimal? Price, bool? InStock, string? Title, bool? TitleMatches,
    bool InRange, string Message, bool AlreadyAdded);
public sealed record AddPage(string Name, string Url, bool RunOnPc);
public sealed record AddPagesRequest(int ProductId, List<AddPage> Pages, bool SetupWithAi);
public sealed record AddPagesResponse(List<int> Added, List<string> Skipped, bool SettingUpWithAi);

/// <summary>Finding a product's pages: the AI searches the web for them, or links are pasted; each is checked, then the
/// chosen ones are added together.</summary>
[ApiController]
[Route("api/find-pages")]
public sealed class FindPagesController(AppDbContext db, OpenAiClient ai, PageFetcher fetcher, SettingsStore settingsStore,
    IServiceScopeFactory scopes, PageSetup setup, Notifier notifier, Tracker tracker) : ControllerBase
{
    private const string Instructions = """
        You find online stores' product pages for a product, using web search. Only the product itself, sold new by the store
        (or the store's own marketplace listing for it), in the given country. Not accessories, not other models, not used or
        refurbished, not search or category pages, not news or reviews. Prefer the stores' own product pages (one per store,
        or a few when a store has separate listings, e.g. a bundle). Answer with JSON only:
        { "pages": [ { "store": "Best Buy", "url": "https://…", "title": "the listing's title", "price": "$1,099.99 or null", "note": "short: in stock? bundle?" } ] }
        Up to 25 pages. Every url must be a real product page you found, copied exactly.
        """;

    private static readonly Dictionary<string, string> Countries = new()
    {
        ["CAD"] = "Canada", ["USD"] = "the United States", ["GBP"] = "the United Kingdom", ["EUR"] = "the European Union", ["AUD"] = "Australia",
    };

    /// <summary>The AI searches the web for the product's pages (one call with web search; logged with its tokens).</summary>
    [HttpPost("search")]
    public async Task<ActionResult<List<FoundPage>>> Search(FindRequest request, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId, ct);
        if (product == null)
            return NotFound();
        var settings = await settingsStore.GetAsync(ct);
        var existing = await ExistingAsync(product.Id, ct);
        var country = Countries.GetValueOrDefault(settings.Currency, "the owner's country");
        var question = $"""
            Product: {product.Name}. The listing's title must match: {product.RequiredKeywords}; it must not be: {product.ExcludedKeywords}.
            Country: {country} (prices in {settings.Currency}). Usual price: {product.MinPrice:0}–{product.MaxPrice:0} {settings.Currency}.
            Already watched (leave these out): {(existing.Count == 0 ? "none" : string.Join(", ", existing.Take(40)))}.
            {(string.IsNullOrWhiteSpace(request.Hint) ? "" : "Also: " + request.Hint.Trim())}
            """;
        JsonElement answer;
        try
        {
            answer = await ai.SearchWebJsonAsync("find-pages", Instructions, question, ct);
        }
        catch (OpenAiClient.AiException e)
        {
            return Problem(e.Message, statusCode: 400);
        }

        var found = new List<FoundPage>();
        if (answer.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            foreach (var p in pages.EnumerateArray())
            {
                var url = Str(p, "url");
                if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    continue;
                var clean = Clean(uri);
                if (found.Any(f => Normal(f.Url) == Normal(clean)))
                    continue;
                found.Add(new FoundPage(Str(p, "store") ?? NameFor(clean), clean, Str(p, "title"), Str(p, "price"), Str(p, "note"),
                    existing.Contains(Normal(clean))));
            }
        return found;
    }

    /// <summary>A quick check of each link (no AI): does it read, is it blocked, is it the product, is it in range.</summary>
    [HttpPost("probe")]
    public async Task<ActionResult<List<ProbedPage>>> Probe(ProbeManyRequest request, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId, ct);
        if (product == null)
            return NotFound();
        var settings = await settingsStore.GetAsync(ct);
        var existing = await ExistingAsync(product.Id, ct);
        var urls = request.Urls.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct().Take(60).ToList();
        using var slots = new SemaphoreSlim(Math.Max(2, settings.MaxConcurrentChecks));
        var results = await Task.WhenAll(urls.Select(async raw =>
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return new ProbedPage(raw, raw, "error", null, null, null, null, false, "Not a web address.", false);
            var url = Clean(uri);
            var name = NameFor(url);
            var already = existing.Contains(Normal(url));
            await slots.WaitAsync(ct);
            try
            {
                var site = SiteFor(url, product.Id, name, runOnPc: false);
                var r = await fetcher.FetchAsync(site, [], settings, ct);
                bool? titleOk = r.Title == null ? null : BuyRules.TitleMatches(product, r.Title, out _);
                var reads = r.Price != null && r.InStock != null;
                var inRange = BuyRules.Evaluate(product, site, r) is { Match: true };
                var message = !reads
                    ? "Read, but no price or stock found: the AI can set it up."
                    : titleOk == false ? $"Title doesn't look like a {product.Name}: \"{r.Title}\"." : r.InStock == true ? "In stock." : "Out of stock.";
                return new ProbedPage(url, name, reads ? "reads" : "unread", r.Price, r.InStock, r.Title, titleOk, inRange, message, already);
            }
            catch (BlockedException e)
            {
                return new ProbedPage(url, name, "blocked", null, null, null, null, false, e.Message.Split(". Stores")[0] + ": use Run on my PC.", already);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                return new ProbedPage(url, name, "error", null, null, null, null, false, e is TaskCanceledException ? "Timed out." : e.Message.Split('\n')[0], already);
            }
            finally
            {
                slots.Release();
            }
        }));
        return results.ToList();
    }

    /// <summary>Adds the chosen pages (enabled, auto-buy off). With setupWithAi, the ones that don't read on their own are set
    /// up by the AI afterwards, one by one, in the background (within the daily token limit).</summary>
    [HttpPost("add")]
    public async Task<ActionResult<AddPagesResponse>> Add(AddPagesRequest request, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == request.ProductId, ct);
        if (product == null)
            return NotFound();
        var existing = await ExistingAsync(product.Id, ct);
        var added = new List<Site>();
        var skipped = new List<string>();
        foreach (var p in request.Pages)
        {
            if (!Uri.TryCreate(p.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                skipped.Add($"{p.Url}: not a web address");
                continue;
            }
            var url = Clean(uri);
            if (!existing.Add(Normal(url)))
            {
                skipped.Add($"{url}: already added");
                continue;
            }
            var site = SiteFor(url, product.Id, string.IsNullOrWhiteSpace(p.Name) ? NameFor(url) : p.Name.Trim(), p.RunOnPc);
            site.Enabled = true;
            site.CreatedAt = DateTimeOffset.UtcNow;
            db.Sites.Add(site);
            added.Add(site);
        }
        await db.SaveChangesAsync(ct);
        foreach (var s in added)
            tracker.CheckSoon(s.Id);
        await notifier.LogAsync(EventLevel.Info, $"Added {added.Count} page{(added.Count == 1 ? "" : "s")} for {product.Name}: {string.Join(", ", added.Select(s => s.Name))}.");

        var ids = added.Select(s => s.Id).ToList();
        var withAi = request.SetupWithAi && ids.Count > 0 && await ai.AvailableAsync(ct);
        if (withAi)
            _ = Task.Run(() => SetUpAsync(ids));
        return new AddPagesResponse(ids, skipped, withAi);
    }

    /// <summary>The added pages that don't read on their own: the AI sets each up (stopping if the token limit is reached).</summary>
    private async Task SetUpAsync(List<int> ids)
    {
        foreach (var id in ids)
        {
            using var scope = scopes.CreateScope();
            var sdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var site = await sdb.Sites.Include(s => s.Product).FirstOrDefaultAsync(s => s.Id == id);
            if (site?.Product == null)
                continue;
            var settings = await settingsStore.GetAsync();
            try
            {
                var r = await fetcher.FetchAsync(site, [], settings, CancellationToken.None);
                if (r.Price != null && r.InStock != null)
                    continue; // Reads already: no AI needed.
            }
            catch (Exception)
            {
                // Blocked or failing: the AI can't do better from the server (unless it's set to run on the PC).
                if (!site.RunOnPc)
                    continue;
            }
            try
            {
                var s = await setup.SuggestAsync(site.Url, site.Product, [], site.Id, "setup", CancellationToken.None, site.RunOnPc);
                if (s.Works && s.RightProduct != false)
                {
                    site.Kind = s.Kind;
                    site.PriceSelector = s.PriceSelector;
                    site.TitleSelector = s.TitleSelector;
                    site.InStockSelector = s.InStockSelector;
                    site.OutOfStockText = s.OutOfStockText;
                    await sdb.SaveChangesAsync();
                    await notifier.LogAsync(EventLevel.Good, $"AI set up {site.Name}: {s.Result!.Price:0.00}, {(s.Result.InStock == true ? "in stock" : "out of stock")}.", site.Id);
                }
                else
                    await notifier.LogAsync(EventLevel.Warning, $"AI couldn't set up {site.Name}: {s.Error ?? s.Notes}", site.Id);
            }
            catch (OpenAiClient.AiException e)
            {
                await notifier.LogAsync(EventLevel.Warning, $"Stopped setting up the new pages with AI: {e.Message}", site.Id);
                return;
            }
            catch (Exception e)
            {
                await notifier.LogAsync(EventLevel.Warning, $"AI couldn't set up {site.Name}: {e.Message.Split('\n')[0]}", site.Id);
            }
        }
    }

    private static Site SiteFor(string url, int productId, string name, bool runOnPc) =>
        StorePresets.For(url) is { } preset
            ? new Site
            {
                Url = url, ProductId = productId, Name = name, RunOnPc = runOnPc, Kind = preset.Kind, PriceSelector = preset.Price,
                TitleSelector = preset.Title, InStockSelector = preset.InStock, OutOfStockText = preset.OutOfStockText,
            }
            : new Site { Url = url, ProductId = productId, Name = name, RunOnPc = runOnPc, Kind = SiteKind.Auto };

    private async Task<HashSet<string>> ExistingAsync(int productId, CancellationToken ct) =>
        (await db.Sites.Where(s => s.ProductId == productId).Select(s => s.Url).ToListAsync(ct)).Select(Normal).ToHashSet();

    /// <summary>The address without tracking parts (utm…, ref), so the same page isn't added twice.</summary>
    private static string Clean(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in query.AllKeys.Where(k => k != null && (k.StartsWith("utm_") || k is "ref" or "ref_" or "tag" or "srsltid" or "gclid")).ToList())
            query.Remove(key);
        var b = new UriBuilder(uri) { Fragment = "", Query = query.ToString() ?? "" };
        return b.Uri.ToString();
    }

    private static string Normal(string url) => url.Trim().TrimEnd('/').ToLowerInvariant().Replace("://www.", "://");

    private static readonly Dictionary<string, string> Names = new()
    {
        ["bestbuy"] = "Best Buy", ["ebgames"] = "EB Games", ["gamestop"] = "GameStop", ["amazon"] = "Amazon", ["walmart"] = "Walmart",
        ["costco"] = "Costco", ["canadacomputers"] = "Canada Computers", ["staples"] = "Staples", ["visions"] = "Visions",
        ["memoryexpress"] = "Memory Express", ["londondrugs"] = "London Drugs", ["canadiantire"] = "Canadian Tire", ["toysrus"] = "Toys\"R\"Us",
        ["newegg"] = "Newegg", ["thebrick"] = "The Brick", ["playstation"] = "PlayStation Direct", ["target"] = "Target",
    };

    public static string NameFor(string url)
    {
        var host = new Uri(url).Host.ToLowerInvariant().Replace("www.", "");
        if (System.Net.IPAddress.TryParse(host, out _))
            return host;
        var label = host.Split('.').Reverse().Skip(1).FirstOrDefault() ?? host;
        if (host.Contains("direct.playstation"))
            label = "playstation";
        return Names.TryGetValue(label, out var n) ? n : char.ToUpperInvariant(label[0]) + label[1..];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
}

using System.Globalization;
using System.Text.Json;
using DeficitSnatcher.Api.Controllers;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Ai;

public sealed record RestockSource(string Title, string Url);

public sealed record RestockForecast(
    /// <summary>When the next restock looks likely; null when there's nothing to go on.</summary>
    DateTimeOffset? NextAt,
    /// <summary>"Thu Nov 21, around 11:00" in the owner's time zone.</summary>
    string? When,
    /// <summary>low, medium, high.</summary>
    string Confidence,
    /// <summary>history, online, both, none.</summary>
    string Basis,
    string Explanation,
    bool InStockNow,
    /// <summary>The restocks this page has seen (came back into stock), newest first.</summary>
    List<DateTimeOffset> Restocks,
    /// <summary>The pattern in its history, e.g. "mostly Thursdays, around 11:00".</summary>
    string? HistoryPattern,
    string? OnlinePattern,
    string? OnlineSummary,
    List<RestockSource> Sources,
    DateTimeOffset? OnlineCheckedAt,
    string? OnlineError,
    string TimeZone);

/// <summary>
/// A guess at a page's next restock: from its own history (when it came back into stock: weekday, time of day, the gap
/// between restocks) and from what ChatGPT finds online about the store (web search, kept a week per store): restock
/// trackers such as NowInStock, forums, Reddit, restock accounts, the store's own news, and stores near the owner's
/// address. Guesses, shown as such.
/// </summary>
public sealed class RestockForecaster(IServiceScopeFactory scopes, OpenAiClient ai, SettingsStore settingsStore, ILogger<RestockForecaster> log)
{
    private static readonly string[] Levels = ["low", "medium", "high"];

    public async Task<RestockForecast?> ForecastAsync(int siteId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site == null)
            return null;
        var settings = await settingsStore.GetAsync(ct);
        var zone = Zone(settings.TimeZone);
        var points = await db.PricePoints.AsNoTracking().Where(p => p.SiteId == siteId).OrderBy(p => p.At)
            .Select(p => new { p.At, p.InStock }).ToListAsync(ct);
        var restocks = new List<DateTimeOffset>();
        for (var i = 1; i < points.Count; i++)
            if (points[i].InStock && !points[i - 1].InStock)
                restocks.Add(points[i].At);
        var insight = await db.RestockInsights.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProductId == site.ProductId && x.Domain == FindPagesController.Domain(site.Url), ct);
        return Combine(restocks, insight, site.LastInStock == true, zone, settings.TimeZone);
    }

    /// <summary>The short forecast for each page of a product (for its list).</summary>
    public async Task<Dictionary<int, RestockForecast>> ForecastProductAsync(int productId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = await db.Sites.Where(s => productId == 0 || s.ProductId == productId).Select(s => s.Id).ToListAsync(ct);
        var result = new Dictionary<int, RestockForecast>();
        foreach (var id in ids)
            if (await ForecastAsync(id, ct) is { } f)
                result[id] = f;
        return result;
    }

    private static RestockForecast Combine(List<DateTimeOffset> restocks, RestockInsight? online, bool inStockNow, TimeZoneInfo zone, string zoneName)
    {
        var now = DateTimeOffset.UtcNow;
        var sources = online == null ? [] : JsonSerializer.Deserialize<List<RestockSource>>(online.Sources, Buying.Buyer.JsonOptions) ?? [];

        // History: the usual weekday and hour, and the usual gap.
        DateTimeOffset? historyNext = null;
        string? historyPattern = null;
        var historyConfidence = "low";
        if (restocks.Count >= 2)
        {
            var local = restocks.Select(r => TimeZoneInfo.ConvertTime(r, zone)).ToList();
            var byDay = local.GroupBy(l => l.DayOfWeek).OrderByDescending(g => g.Count()).First();
            var share = (double)byDay.Count() / local.Count;
            // To the quarter hour: a forecast, not a timestamp.
            var hour = Math.Round(Median(byDay.Select(l => l.Hour + l.Minute / 60.0).ToList()) * 4) / 4;
            var gaps = restocks.Zip(restocks.Skip(1), (a, b) => (b - a).TotalDays).ToList();
            var gap = Median(gaps);
            if (share >= 0.5 && local.Count >= 3)
            {
                historyNext = NextWeekday(byDay.Key, hour, zone, now);
                historyPattern = $"{(share >= 0.8 ? "almost always" : "mostly")} {byDay.Key}s, around {Clock(hour)} ({byDay.Count()} of {local.Count} restocks)";
                historyConfidence = local.Count >= 5 && share >= 0.6 ? "high" : "medium";
            }
            else
            {
                var next = restocks[^1].AddDays(gap);
                while (next < now)
                    next = next.AddDays(Math.Max(gap, 1));
                historyNext = next;
                historyPattern = $"every {Days(gap)} or so ({local.Count} restocks, no favourite day)";
                historyConfidence = "low";
            }
        }

        DateTimeOffset? onlineNext = online?.NextLikely is { } n && n > now ? n : null;
        var onlineConfidence = online?.Confidence is "medium" or "high" ? online.Confidence : "low";

        DateTimeOffset? nextAt;
        string basis, confidence, explanation;
        if (onlineNext != null && historyNext != null && Math.Abs((onlineNext.Value - historyNext.Value).TotalHours) <= 24)
        {
            nextAt = onlineNext;
            basis = "both";
            confidence = Levels[Math.Min(2, Math.Max(Array.IndexOf(Levels, onlineConfidence), Array.IndexOf(Levels, historyConfidence)) + 1)];
            explanation = $"Its own history ({historyPattern}) and what's reported online agree.";
        }
        else if (onlineNext != null && historyNext != null)
        {
            // They disagree: the sooner one (better to be ready early), with the other mentioned.
            var historyFirst = historyNext < onlineNext;
            nextAt = historyFirst ? historyNext : onlineNext;
            basis = historyFirst ? "history" : "online";
            confidence = historyFirst ? historyConfidence : onlineConfidence;
            var zoneNow = now;
            explanation = historyFirst
                ? $"From the restocks seen here: {historyPattern}. Online reports point later: {online!.Pattern ?? Format(TimeZoneInfo.ConvertTime(onlineNext.Value, zone), zoneNow, zone)}."
                : $"From reports online: {online!.Pattern}. Its own history points later ({historyPattern}).";
        }
        else if (onlineNext != null)
        {
            nextAt = onlineNext;
            basis = "online";
            confidence = onlineConfidence;
            explanation = online!.Pattern is { Length: > 0 } p ? $"From reports online: {p}." : "From reports online.";
        }
        else if (historyNext != null)
        {
            nextAt = historyNext;
            basis = "history";
            confidence = historyConfidence;
            explanation = $"From the restocks seen here: {historyPattern}.";
        }
        else
        {
            nextAt = null;
            basis = "none";
            confidence = "low";
            explanation = restocks.Count == 0
                ? "No restocks seen here yet: the forecast learns from each one it sees."
                : "Only one restock seen here so far: not enough to guess from yet.";
            if (online?.Pattern is { Length: > 0 } p)
                explanation += $" Online: {p}.";
        }

        var when = nextAt is { } at ? Format(TimeZoneInfo.ConvertTime(at, zone), now, zone) : null;
        return new RestockForecast(nextAt, when, confidence, basis, explanation, inStockNow, [.. restocks.AsEnumerable().Reverse().Take(30)],
            historyPattern, online?.Pattern, online?.Summary, sources, online?.FetchedAt, online?.Error, zoneName);
    }

    private const string Instructions = """
        You research when an online store restocks a product, using web search. Search several kinds of source, not just
        one, and weigh them together:
        - Restock trackers, first: NowInStock (nowinstock.net: the product's tracker page for the owner's country, and its
          stock history log with dates and times), StockTrack (stocktrack.ca in Canada), HotStock, and similar trackers.
        - Restock alert accounts and groups: X/Twitter, Discord and Telegram restock alerts, as reported or archived publicly.
        - Forums and Reddit: deal forums (RedFlagDeals in Canada, Slickdeals in the US, HotUKDeals in the UK) and the
          product's and store's subreddits, for recent "it's back" posts with times.
        - The store itself: its announcements, launch or drop dates, flyers, and how it releases stock (online drops, queues).
        - Near the owner: stores of this chain near the owner's area (given below) and whether they restock in store or for
          local pickup, and regional patterns (e.g. a province's stores getting stock on another day).
        Prefer recent reports (the last few months) over old ones, and dated tracker logs over hearsay. Convert times to the
        owner's time zone. Answer with JSON only:
        {
          "pattern": "the usual pattern in a few words, e.g. 'Thursdays around 11 am ET, every 1–2 weeks', or null if none is known",
          "nextLikely": "ISO 8601 date and time of the next likely restock (with its UTC offset), or null if nothing points to one",
          "confidence": "low | medium | high (high only when several independent sources agree)",
          "summary": "two or three sentences: what the recent reports say, which kinds of source, and how sure that is",
          "nearYou": "one or two sentences about stores near the owner's area (in-store or pickup restocks), or null if nothing found",
          "sources": [ { "title": "…", "url": "https://…" } ]
        }
        Only real reports you found, with their links (up to 10, from as many different kinds of source as you can). If
        there's little to go on, say so with low confidence.
        """;

    /// <summary>Looks the store's restock pattern up online (one web search) and keeps it for the product and store.</summary>
    public async Task<string?> RefreshOnlineAsync(int productId, string url, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = await db.Products.AsNoTracking().FirstAsync(p => p.Id == productId, ct);
        var settings = await settingsStore.GetAsync(ct);
        var profile = await settingsStore.GetProfileAsync(ct);
        var home = StoreRegion.For(profile.Country, settings.Currency);
        var domain = FindPagesController.Domain(url);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone(settings.TimeZone));
        // Where the owner is: the city, province and the start of the postal code (its area), never the street.
        var city = profile.City.Trim();
        var province = profile.Province.Trim();
        var postalArea = PostalArea(profile.PostalCode, home);
        var area = string.Join(", ", new[] { city, province, postalArea is null ? "" : $"postal area {postalArea}", home?.Name ?? "" }
            .Where(x => x.Length > 0));
        var question = $"""
            Store: {FindPagesController.NameFor(url)} ({domain}){(home != null ? $", in {home.Name}" : "")}. Product: {product.Name} (the product page: {url}).
            The owner is in: {(area.Length > 0 ? area : "(not set)")}.
            Now: {now:dddd yyyy-MM-dd HH:mm} ({settings.TimeZone}). When does this store usually restock it, and when is the next restock likely,
            online and at its stores near the owner?
            """;
        var near = new SearchLocation(home?.Code switch { null => null, "uk" => "GB", var c => c.ToUpperInvariant() },
            province.Length > 0 ? province : null, city.Length > 0 ? city : null, settings.TimeZone);
        var row = await db.RestockInsights.FirstOrDefaultAsync(x => x.ProductId == productId && x.Domain == domain, ct);
        if (row == null)
        {
            row = new RestockInsight { ProductId = productId, Domain = domain };
            db.RestockInsights.Add(row);
        }
        row.FetchedAt = DateTimeOffset.UtcNow;
        try
        {
            var a = await ai.SearchWebJsonAsync("restock-forecast", Instructions, question, ct, near);
            row.Pattern = Str(a, "pattern");
            row.NextLikely = Str(a, "nextLikely") is { } next && DateTimeOffset.TryParse(next, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : null;
            row.Confidence = Str(a, "confidence") is "medium" or "high" ? Str(a, "confidence")! : "low";
            row.Summary = Str(a, "summary") ?? "";
            if (Str(a, "nearYou") is { } local)
                row.Summary = (row.Summary + $" Near you{(city.Length > 0 ? $" ({city})" : "")}: {local}").Trim();
            var sources = new List<RestockSource>();
            if (a.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Array)
                foreach (var s in src.EnumerateArray().Take(10))
                    if (Str(s, "url") is { } u && Uri.TryCreate(u, UriKind.Absolute, out _))
                        sources.Add(new RestockSource(Str(s, "title") ?? u, u));
            row.Sources = JsonSerializer.Serialize(sources, Buying.Buyer.JsonOptions);
            row.Error = null;
        }
        catch (OpenAiClient.AiException e)
        {
            row.Error = e.Message.Length > 1000 ? e.Message[..1000] : e.Message;
        }
        await db.SaveChangesAsync(ct);
        return row.Error;
    }

    /// <summary>The background part: one store whose online forecast is missing or a week old, per call (hourly).</summary>
    public async Task RefreshOneStaleAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        if (!settings.AiRestockForecast || !await ai.AvailableAsync(ct))
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sites = await db.Sites.AsNoTracking().Where(s => s.Enabled && s.Product!.Enabled).Select(s => new { s.ProductId, s.Url }).ToListAsync(ct);
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var known = await db.RestockInsights.AsNoTracking().Where(x => x.FetchedAt > weekAgo).Select(x => new { x.ProductId, x.Domain }).ToListAsync(ct);
        var stale = sites.FirstOrDefault(s => !known.Any(k => k.ProductId == s.ProductId && k.Domain == FindPagesController.Domain(s.Url)));
        if (stale == null)
            return;
        log.LogInformation("Looking up {Store}'s restock pattern online", FindPagesController.Domain(stale.Url));
        await RefreshOnlineAsync(stale.ProductId, stale.Url, ct);
    }

    // ---- Helpers ----

    /// <summary>The area part of a postal code: Canada's first three characters (M5V), the UK's outward code (SW1A), a US
    /// ZIP's first three digits. Null when there's none.</summary>
    private static string? PostalArea(string postalCode, StoreRegion.Country? home)
    {
        var p = postalCode.Trim().ToUpperInvariant();
        if (p.Length < 3)
            return null;
        return home?.Code switch
        {
            "uk" => p.Contains(' ') ? p[..p.IndexOf(' ')] : p.Length > 3 ? p[..^3] : p,
            "us" => p[..3],
            _ => p.Replace(" ", "")[..3],
        };
    }

    public static TimeZoneInfo Zone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) ? z : TimeZoneInfo.TryFindSystemTimeZoneById("America/Toronto", out var t) ? t : TimeZoneInfo.Utc;

    private static DateTimeOffset NextWeekday(DayOfWeek day, double hour, TimeZoneInfo zone, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var date = local.Date;
        for (var i = 0; i < 8; i++)
        {
            var candidate = date.AddDays(i);
            if (candidate.DayOfWeek != day)
                continue;
            var at = candidate.AddHours(hour);
            var offset = zone.GetUtcOffset(at);
            var result = new DateTimeOffset(at, offset);
            if (result > now)
                return result;
        }
        var fallback = date.AddDays(7).AddHours(hour);
        return new DateTimeOffset(fallback, zone.GetUtcOffset(fallback));
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0)
            return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static string Clock(double hour)
    {
        var h = (int)Math.Floor(hour);
        var m = (int)Math.Round((hour - h) * 60 / 15) * 15;
        if (m == 60)
            (h, m) = (h + 1, 0);
        return $"{h % 24:00}:{m:00}";
    }

    private static string Days(double d) => d < 1.5 ? "day" : d < 10 ? $"{d:0} days" : $"{d / 7:0.#} weeks";

    private static string Format(DateTimeOffset local, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var day = local.Date == today ? "Today" : local.Date == today.AddDays(1) ? "Tomorrow" : local.ToString("ddd MMM d", CultureInfo.InvariantCulture);
        return $"{day}, around {local:HH:mm}";
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
}

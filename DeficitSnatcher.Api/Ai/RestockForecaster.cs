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
    /// <summary>history, online, both, others (the product's other stores), typical (a general guess), none.</summary>
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
    bool OnlineLookupAvailable,
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
        var domain = FindPagesController.Domain(site.Url);
        var insights = await db.RestockInsights.AsNoTracking().Where(x => x.ProductId == site.ProductId).ToListAsync(ct);
        var insight = insights.FirstOrDefault(x => x.Domain == domain);

        // The product's other pages: their restocks and what's reported online, for when this one has nothing to go on.
        var otherIds = await db.Sites.AsNoTracking().Where(s => s.ProductId == site.ProductId && s.Id != siteId).Select(s => s.Id).ToListAsync(ct);
        var otherPoints = await db.PricePoints.AsNoTracking()
            .Where(p => otherIds.Contains(p.SiteId)).OrderBy(p => p.SiteId).ThenBy(p => p.At)
            .Select(p => new { p.SiteId, p.At, p.InStock }).ToListAsync(ct);
        var otherRestocks = new List<DateTimeOffset>();
        for (var i = 1; i < otherPoints.Count; i++)
            if (otherPoints[i].SiteId == otherPoints[i - 1].SiteId && otherPoints[i].InStock && !otherPoints[i - 1].InStock)
                otherRestocks.Add(otherPoints[i].At);
        otherRestocks.Sort();
        var now = DateTimeOffset.UtcNow;
        var otherOnline = insights.Where(x => x.Domain != domain && x.NextLikely > now).OrderBy(x => x.NextLikely)
            .Select(x => (x.Domain, x.NextLikely!.Value)).FirstOrDefault();

        return Combine(restocks, insight, site.LastInStock == true, zone, settings.TimeZone,
            settings.AiRestockForecast && settings.AiEnabled && ai.HasKey, otherRestocks,
            otherOnline.Domain == null ? null : otherOnline);
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

    private static RestockForecast Combine(List<DateTimeOffset> restocks, RestockInsight? online, bool inStockNow, TimeZoneInfo zone, string zoneName,
        bool onlineLookupAvailable, List<DateTimeOffset> otherRestocks, (string Domain, DateTimeOffset At)? otherOnline)
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
        else if (online?.NextLikely is { } past && Math.Max(1, Math.Ceiling((now - past).TotalDays / 7)) is var weeks && weeks <= 8)
        {
            // Online reports pointed to a time that's gone: the same time the next week (until it's looked up again).
            nextAt = past.AddDays(7 * weeks);
            basis = "online";
            confidence = "low";
            explanation = $"Online reports pointed to {Format(TimeZoneInfo.ConvertTime(past, zone), now, zone)}{(online.Pattern is { Length: > 0 } p ? $" ({p})" : "")}: "
                + "the same time a week on.";
        }
        else if (restocks.Count == 1)
        {
            var last = TimeZoneInfo.ConvertTime(restocks[0], zone);
            var hour = Math.Round((last.Hour + last.Minute / 60.0) * 4) / 4;
            nextAt = NextWeekday(last.DayOfWeek, hour, zone, now);
            basis = "history";
            confidence = "low";
            explanation = $"Only one restock seen here so far ({last.DayOfWeek}, around {Clock(hour)}): the same day and time again.";
        }
        else if (otherOnline != null || otherRestocks.Count >= 2)
        {
            // Nothing for this store yet: what the product's other stores point to (restocks often come in waves).
            var fromOthers = otherRestocks.Count >= 2 ? HistoryGuess(otherRestocks, zone, now) : null;
            var useOnline = otherOnline != null && (fromOthers == null || otherOnline.Value.At < fromOthers.Value.Next);
            nextAt = useOnline ? otherOnline!.Value.At : fromOthers!.Value.Next;
            basis = "others";
            confidence = "low";
            explanation = (restocks.Count == 0 ? "No restocks seen here yet" : "Not enough seen here yet")
                + (useOnline
                    ? $": from {otherOnline!.Value.Domain}, where a restock is reported likely then (restocks often come in waves)."
                    : $": from this product's other stores ({fromOthers!.Value.Pattern}).");
        }
        else
        {
            // Nothing at all to go on: most online stores drop stock on a weekday morning. A placeholder until it learns.
            nextAt = NextWeekdayMorning(zone, now);
            basis = "typical";
            confidence = "low";
            explanation = "Nothing to go on for this store yet, so a general guess: most stores drop stock on weekday mornings. "
                + "It gets better with each restock seen here and each online lookup.";
        }
        if (basis is not ("history" or "both") && online?.Pattern is { Length: > 0 } op && !explanation.Contains(op))
            explanation += $" Online: {op}.";

        var when = nextAt is { } at ? Format(TimeZoneInfo.ConvertTime(at, zone), now, zone) : null;
        return new RestockForecast(nextAt, when, confidence, basis, explanation, inStockNow, [.. restocks.AsEnumerable().Reverse().Take(30)],
            historyPattern, online?.Pattern, online?.Summary, sources, online?.FetchedAt, online?.Error, onlineLookupAvailable, zoneName);
    }

    private const string Instructions = """
        You research when an online store restocks a product, using web search. Search several kinds of source, not just
        one, and weigh them together:
        - Restock trackers, first: NowInStock (nowinstock.net: the product's tracker page for the owner's country, and its
          stock history log with dates and times), StockTrack (stocktrack.ca in Canada), HotStock, and similar trackers.
        - Restock alert accounts and groups: well-known restock accounts on X/Twitter, Threads and Bluesky (e.g. Wario64, and
          the country's own PS5/console restock accounts), Discord and Telegram restock groups as reported or archived
          publicly, and Facebook groups.
        - Live restock streams and videos: YouTube/Twitch restock streamers (e.g. Jake Randall style streams) and their
          posted drop times.
        - News restock roundups: gaming and tech sites' "where to buy / restock tracker" articles (IGN, GameSpot, The Verge,
          and the country's own, e.g. MobileSyrup or iPhone in Canada), which often list drop days and times.
        - Forums and Reddit: deal forums (RedFlagDeals in Canada, Slickdeals in the US, HotUKDeals in the UK), the product's
          subreddits (e.g. r/PS5, r/PS5Canada) and the store's (e.g. r/bestbuycanada), for recent "it's back" posts with times.
        - The store itself: its announcements, launch or drop dates, its weekly flyer cycle (which day new deals start), and
          how it releases stock (online drops, queues, time of day).
        - Near the owner: stores of this chain near the owner's area (given below) and whether they restock in store or for
          local pickup, and regional patterns (e.g. a province's stores getting stock on another day).
        Prefer recent reports (the last few months) over old ones, and dated tracker logs over hearsay. Convert times to the
        owner's time zone. Answer with JSON only:
        {
          "pattern": "the usual pattern in a few words, e.g. 'Thursdays around 11 am ET, every 1–2 weeks', or null if none is known",
          "nextLikely": "ISO 8601 date and time (with its UTC offset) of the next likely restock AFTER the given 'Now'. When there's any
                         pattern at all, even a rough one (a usual weekday and time, a usual gap since the last drop), give its next
                         occurrence after Now with low confidence rather than null; never a time that's already past. null only
                         when nothing at all is known",
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

    /// <summary>The background part, one store per call (every few minutes): one never looked up first, then one whose
    /// predicted restock has passed (looked up again after 6 hours), then one a week old.</summary>
    public async Task RefreshOneStaleAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        if (!settings.AiRestockForecast || !await ai.AvailableAsync(ct))
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sites = await db.Sites.AsNoTracking().Where(s => s.Enabled && s.Product!.Enabled).Select(s => new { s.ProductId, s.Url }).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var rows = await db.RestockInsights.AsNoTracking().Select(x => new { x.ProductId, x.Domain, x.FetchedAt, x.NextLikely }).ToListAsync(ct);
        bool Fresh(int productId, string url, Func<DateTimeOffset, DateTimeOffset?, bool> ok) =>
            rows.Any(r => r.ProductId == productId && r.Domain == FindPagesController.Domain(url) && ok(r.FetchedAt, r.NextLikely));
        var stale = sites.FirstOrDefault(s => !Fresh(s.ProductId, s.Url, (_, _) => true))
            ?? sites.FirstOrDefault(s => !Fresh(s.ProductId, s.Url, (at, next) => !(next < now && now - at > TimeSpan.FromHours(6))))
            ?? sites.FirstOrDefault(s => !Fresh(s.ProductId, s.Url, (at, _) => now - at < TimeSpan.FromDays(7)));
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

    /// <summary>The next restock from a list of restocks (oldest first, 2 or more): the usual weekday and hour, or the usual gap.</summary>
    private static (DateTimeOffset Next, string Pattern)? HistoryGuess(List<DateTimeOffset> restocks, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (restocks.Count < 2)
            return null;
        var local = restocks.Select(r => TimeZoneInfo.ConvertTime(r, zone)).ToList();
        var byDay = local.GroupBy(l => l.DayOfWeek).OrderByDescending(g => g.Count()).First();
        var share = (double)byDay.Count() / local.Count;
        var hour = Math.Round(Median(byDay.Select(l => l.Hour + l.Minute / 60.0).ToList()) * 4) / 4;
        if (share >= 0.5 && local.Count >= 3)
            return (NextWeekday(byDay.Key, hour, zone, now), $"mostly {byDay.Key}s, around {Clock(hour)}");
        var gap = Math.Max(Median(restocks.Zip(restocks.Skip(1), (a, b) => (b - a).TotalDays).ToList()), 1);
        var next = restocks[^1].AddDays(gap);
        while (next < now)
            next = next.AddDays(gap);
        return (next, $"every {Days(gap)} or so");
    }

    /// <summary>The next weekday (Monday to Friday) at 10:00, local time.</summary>
    private static DateTimeOffset NextWeekdayMorning(TimeZoneInfo zone, DateTimeOffset now)
    {
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var i = 0; ; i++)
        {
            var at = date.AddDays(i).AddHours(10);
            if (at.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;
            var result = new DateTimeOffset(at, zone.GetUtcOffset(at));
            if (result > now)
                return result;
        }
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

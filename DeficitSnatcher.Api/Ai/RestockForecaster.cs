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
    /// <summary>A time marker (see Times) the site shows as "Thu Nov 21, around 11:00 AM" in the viewer's time.</summary>
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

    /// <summary>How long after a predicted restock it's still "now" (matches the fast checks' window in Tracker).</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(90);

    public async Task<RestockForecast?> ForecastAsync(int siteId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site == null)
            return null;
        var settings = await settingsStore.GetAsync(ct);
        // All in UTC: the site shows times in the viewer's own zone.
        var zone = TimeZoneInfo.Utc;
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
        foreach (var other in insights.Where(x => x.Domain != domain))
            otherRestocks.AddRange(Events(other));
        otherRestocks.Sort();
        var now = DateTimeOffset.UtcNow;
        var otherOnline = insights.Where(x => x.Domain != domain && x.NextLikely > now).OrderBy(x => x.NextLikely)
            .Select(x => (x.Domain, x.NextLikely!.Value)).FirstOrDefault();

        return Combine(restocks, insight, site.LastInStock == true, zone, "UTC",
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

        // History: the usual weekday and hour, and the usual gap, from the restocks seen here and the dated ones reported
        // online for this store (trackers' logs, posts), the same restock counted once.
        var reported = online == null ? [] : Events(online);
        var seen = restocks.Concat(reported.Where(e => !restocks.Any(r => Math.Abs((r - e).TotalHours) < 3)))
            .OrderBy(x => x).ToList();
        var sourceNote = reported.Count == 0 ? "" : restocks.Count == 0 ? ", reported online" : $": {restocks.Count} seen here, {seen.Count - restocks.Count} reported online";
        DateTimeOffset? historyNext = null;
        string? historyPattern = null;
        var historyConfidence = "low";
        if (seen.Count >= 2)
        {
            var local = seen.Select(r => TimeZoneInfo.ConvertTime(r, zone)).ToList();
            var byDay = local.GroupBy(l => l.DayOfWeek).OrderByDescending(g => g.Count()).First();
            var share = (double)byDay.Count() / local.Count;
            // To the quarter hour: a forecast, not a timestamp.
            var hour = Math.Round(Median(byDay.Select(l => l.Hour + l.Minute / 60.0).ToList()) * 4) / 4;
            var gaps = seen.Zip(seen.Skip(1), (a, b) => (b - a).TotalDays).ToList();
            var gap = Median(gaps);
            if (share >= 0.5 && local.Count >= 3)
            {
                historyNext = NextWeekday(byDay.Key, hour, zone, now);
                historyPattern = $"{(share >= 0.8 ? "almost always" : "mostly")} {Times.At(historyNext.Value, "weekly")} ({byDay.Count()} of {local.Count} restocks{sourceNote})";
                historyConfidence = local.Count >= 5 && share >= 0.6 ? "high" : "medium";
            }
            else
            {
                var next = seen[^1].AddDays(gap);
                while (next < now)
                    next = next.AddDays(Math.Max(gap, 1));
                historyNext = next;
                historyPattern = $"every {Days(gap)} or so ({local.Count} restocks{sourceNote}, no favourite day)";
                historyConfidence = "low";
            }
        }

        // A predicted time stays the forecast until its window is over (90 minutes after it, as the fast checks around it):
        // drops often come late, and jumping a week ahead at the predicted minute would end those checks early.
        DateTimeOffset? onlineNext = online?.NextLikely is { } n && n > now - Window ? n : null;
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
                ? $"From this store's restocks: {historyPattern}. Online reports point later: {online!.Pattern ?? Format(TimeZoneInfo.ConvertTime(onlineNext.Value, zone), zoneNow, zone)}."
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
            explanation = $"From this store's restocks: {historyPattern}.";
        }
        else if (online?.NextLikely is { } past && Math.Max(1, Math.Ceiling((now - past).TotalDays / 7)) is var weeks && weeks <= 8)
        {
            // Online reports pointed to a time that's gone (and its window): the same time the next week, until it's looked
            // up again (which happens soon after a prediction passes, see RefreshOneStaleAsync).
            nextAt = past.AddDays(7 * weeks);
            basis = "online";
            confidence = "low";
            explanation = $"Online reports pointed to {Format(TimeZoneInfo.ConvertTime(past, zone), now, zone)}{(online.Pattern is { Length: > 0 } p ? $" ({p})" : "")}: "
                + "the same time a week on.";
        }
        else if (seen.Count == 1)
        {
            var last = TimeZoneInfo.ConvertTime(seen[0], zone);
            var hour = Math.Round((last.Hour + last.Minute / 60.0) * 4) / 4;
            nextAt = NextWeekday(last.DayOfWeek, hour, zone, now);
            basis = "history";
            confidence = "low";
            explanation = $"Only one restock known for this store so far ({Times.At(seen[0], "weekly")}): the same day and time again.";
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
        Prefer recent reports (the last few months) over old ones, and dated tracker logs over hearsay. Give every time in UTC:
        timestamps as ISO 8601 ending in Z. In "pattern", "summary" and "nearYou", write each clock time as a marker the app
        shows in the reader's own time: {{t:<ISO 8601 UTC>|weekly}} for a usual weekday and time (any one occurrence of it,
        e.g. "{{t:2026-11-19T16:00:00Z|weekly}}" for Thursdays at 16:00 UTC), {{t:<ISO 8601 UTC>|time}} for a time of day,
        {{t:<ISO 8601 UTC>}} for a date and time. Never write a clock time any other way. Answer with JSON only:
        {
          "pattern": "the usual pattern in a few words, e.g. '{{t:2026-11-19T16:00:00Z|weekly}}, every 1–2 weeks', or null if none is known",
          "nextLikely": "ISO 8601 date and time in UTC (ending in Z) of the next likely restock AFTER the given 'Now'. When there's any
                         pattern at all, even a rough one (a usual weekday and time, a usual gap since the last drop), give its next
                         occurrence after Now with low confidence rather than null; never a time that's already past. null only
                         when nothing at all is known",
          "confidence": "low | medium | high (high only when several independent sources agree)",
          "summary": "two or three sentences: what the recent reports say, which kinds of source, and how sure that is",
          "nearYou": "one or two sentences about stores near the owner's area (in-store or pickup restocks), or null if nothing found",
          "restocks": [ "ISO 8601 date and time in UTC (ending in Z) of each past restock of this product at this store that you
                        found a dated report of (a tracker's log entry, a dated post), last 6 months, oldest first; up to 40; [] if none" ],
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
        var now = DateTimeOffset.UtcNow;
        // Where the owner is: the city, province and the start of the postal code (its area), never the street.
        var city = profile.City.Trim();
        var province = profile.Province.Trim();
        var postalArea = PostalArea(profile.PostalCode, home);
        var area = string.Join(", ", new[] { city, province, postalArea is null ? "" : $"postal area {postalArea}", home?.Name ?? "" }
            .Where(x => x.Length > 0));
        var question = $"""
            Store: {FindPagesController.NameFor(url)} ({domain}){(home != null ? $", in {home.Name}" : "")}. Product: {product.Name} (the product page: {url}).
            The owner is in: {(area.Length > 0 ? area : "(not set)")}.
            Now: {now:dddd yyyy-MM-dd HH:mm} UTC. When does this store usually restock it, and when is the next restock likely,
            online and at its stores near the owner?
            """;
        var near = new SearchLocation(home?.Code switch { null => null, "uk" => "GB", var c => c.ToUpperInvariant() },
            province.Length > 0 ? province : null, city.Length > 0 ? city : null, null);
        var row = await db.RestockInsights.FirstOrDefaultAsync(x => x.ProductId == productId && x.Domain == domain, ct);
        if (row == null)
        {
            row = new RestockInsight { ProductId = productId, Domain = domain };
            db.RestockInsights.Add(row);
        }
        row.FetchedAt = DateTimeOffset.UtcNow;
        try
        {
            // First a deep, broad search; then, when it comes back thin (no date, low confidence or few dated restocks), a
            // second one that digs into specific places for dated restocks of this store.
            var a = await ai.SearchWebJsonAsync("restock-forecast", Instructions, question, ct, near, deep: true);
            var pattern = Str(a, "pattern");
            var nextLikely = Date(Str(a, "nextLikely"));
            var confidence = Str(a, "confidence") is "medium" or "high" ? Str(a, "confidence")! : "low";
            var summary = Str(a, "summary") ?? "";
            var nearYou = Str(a, "nearYou");
            var events = Dates(a, "restocks");
            var sources = Sources(a);

            if (nextLikely == null || confidence == "low" || events.Count < 3)
            {
                var dig = $"""
                    {question}
                    A first search found: pattern {pattern ?? "(none)"}; {events.Count} dated restock(s); confidence {confidence}.
                    Summary: {(summary.Length > 0 ? summary : "(nothing)")}
                    Sources already seen: {(sources.Count > 0 ? string.Join(", ", sources.Select(x => x.Url)) : "(none)")}
                    Dig deeper for DATED restocks of this product at {FindPagesController.NameFor(url)}, beyond those sources. Search specifically:
                    NowInStock's page for this product (its history/log table for {domain}); StockTrack and other trackers' pages for it;
                    "{FindPagesController.NameFor(url)} {product.Name} restock" and "in stock" on Reddit, RedFlagDeals/Slickdeals and X/Twitter
                    restock accounts, sorted by recent; YouTube/Twitch restock streams naming {FindPagesController.NameFor(url)}. If this
                    exact product has nothing dated, use the closest variants (other editions/bundles of it) at this store, and say so in
                    the summary. Then give the next restock after Now from all of it (a usual weekday and time, or the usual gap since the
                    last one), with low confidence rather than null whenever there is anything to go on.
                    """;
                try
                {
                    var b = await ai.SearchWebJsonAsync("restock-forecast-dig", Instructions, dig, ct, near, deep: true);
                    var moreEvents = Dates(b, "restocks");
                    events = events.Concat(moreEvents.Where(e => !events.Any(x => Math.Abs((x - e).TotalHours) < 3))).OrderBy(x => x).ToList();
                    var bConfidence = Str(b, "confidence") is "medium" or "high" ? Str(b, "confidence")! : "low";
                    // The dig's answer wins when it has a date and the first didn't, or it's at least as sure.
                    if (Date(Str(b, "nextLikely")) is { } bNext && (nextLikely == null || Array.IndexOf(Levels, bConfidence) >= Array.IndexOf(Levels, confidence)))
                    {
                        nextLikely = bNext;
                        confidence = bConfidence;
                        pattern = Str(b, "pattern") ?? pattern;
                    }
                    else
                        pattern ??= Str(b, "pattern");
                    if (Str(b, "summary") is { } more && more != summary)
                        summary = (summary + " " + more).Trim();
                    nearYou ??= Str(b, "nearYou");
                    sources = sources.Concat(Sources(b)).DistinctBy(x => x.Url).Take(16).ToList();
                }
                catch (OpenAiClient.AiException e)
                {
                    log.LogInformation("The deeper restock search for {Store} failed: {Error}", domain, e.Message);
                }
            }

            row.Pattern = pattern;
            row.NextLikely = nextLikely;
            row.Confidence = confidence;
            row.Summary = summary;
            if (nearYou is { } local)
                row.Summary = (row.Summary + $" Near you{(city.Length > 0 ? $" ({city})" : "")}: {local}").Trim();
            row.Events = JsonSerializer.Serialize(events.Where(e => e <= DateTimeOffset.UtcNow).TakeLast(60).ToList(), Buying.Buyer.JsonOptions);
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
    /// predicted restock has passed (looked up again after 6 hours), then one with no date (again after a day), then one
    /// a week old.</summary>
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
            // A prediction whose window has passed, looked up before it did: looked up again right away (once).
            ?? sites.FirstOrDefault(s => !Fresh(s.ProductId, s.Url, (at, next) => !(next + Window < now && at < next + Window)))
            ?? sites.FirstOrDefault(s => !Fresh(s.ProductId, s.Url, (at, next) => !(next < now && now - at > TimeSpan.FromHours(6))))
            ?? sites.FirstOrDefault(s => !Fresh(s.ProductId, s.Url, (at, next) => !(next == null && now - at > TimeSpan.FromDays(1))))
            ?? sites.FirstOrDefault(s => !Fresh(s.ProductId, s.Url, (at, _) => now - at < TimeSpan.FromDays(7)));
        if (stale == null)
            return;
        log.LogInformation("Looking up {Store}'s restock pattern online", FindPagesController.Domain(stale.Url));
        await RefreshOnlineAsync(stale.ProductId, stale.Url, ct);
    }

    // ---- Helpers ----

    private static DateTimeOffset? Date(string? s) =>
        s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : null;

    private static List<DateTimeOffset> Dates(JsonElement e, string name)
    {
        var list = new List<DateTimeOffset>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray().Take(60))
                if (x.ValueKind == JsonValueKind.String && Date(x.GetString()) is { } d && d > DateTimeOffset.UtcNow.AddYears(-1))
                    list.Add(d);
        return list.OrderBy(x => x).ToList();
    }

    private static List<RestockSource> Sources(JsonElement a)
    {
        var sources = new List<RestockSource>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Array)
            foreach (var s in src.EnumerateArray().Take(10))
                if (Str(s, "url") is { } u && Uri.TryCreate(u, UriKind.Absolute, out _))
                    sources.Add(new RestockSource(Str(s, "title") ?? u, u));
        return sources;
    }

    /// <summary>The past restocks reported online for a store, oldest first.</summary>
    private static List<DateTimeOffset> Events(RestockInsight row)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DateTimeOffset>>(row.Events, Buying.Buyer.JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

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
        {
            var weekly = NextWeekday(byDay.Key, hour, zone, now);
            return (weekly, $"mostly {Times.At(weekly, "weekly")}");
        }
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

    private static string Days(double d) => d < 1.5 ? "day" : d < 10 ? $"{d:0} days" : $"{d / 7:0.#} weeks";

    /// <summary>When, as a marker the site shows as "Today, around 7:00 AM" in the viewer's time.</summary>
    private static string Format(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone) => Times.At(at, "when");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
}

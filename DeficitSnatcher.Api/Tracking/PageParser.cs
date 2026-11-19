using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>What a check read off a product page.</summary>
public sealed record PageResult
{
    public decimal? Price { get; init; }
    /// <summary>null: the page didn't say.</summary>
    public bool? InStock { get; init; }
    public string? Title { get; init; }
    public string? Currency { get; init; }
    public string? VariantId { get; init; }
    /// <summary>Where each value came from (e.g. "json-ld", "selector"), for the site's Test button.</summary>
    public List<string> Notes { get; init; } = [];
    /// <summary>What was fetched (HTML or JSON), for the scan's snapshot; not sent to the web.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Source { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourceType { get; init; }
}

/// <summary>Reads price, stock and title from a Shopify product's JSON or from a page's HTML.</summary>
public static partial class PageParser
{
    private static readonly HtmlParser Html = new();

    /// <summary>A Shopify product's {url}.js: prices are in cents, each variant says whether it can be bought.</summary>
    public static PageResult? ParseShopify(string json, string? wantedVariantId)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array)
                return null;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;

            JsonElement? pick = null;
            foreach (var v in variants.EnumerateArray())
            {
                var id = v.GetProperty("id").ToString();
                if (!string.IsNullOrWhiteSpace(wantedVariantId))
                {
                    if (id == wantedVariantId.Trim())
                    {
                        pick = v;
                        break;
                    }
                    continue;
                }
                pick ??= v;
                if (v.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True)
                {
                    pick = v;
                    break;
                }
            }
            if (pick is not { } chosen)
                return new PageResult { Title = title, InStock = false, Notes = ["shopify: variant not found"] };

            decimal? price = chosen.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() / 100m : null;
            var inStock = chosen.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.True;
            var variantTitle = chosen.TryGetProperty("title", out var vt) ? vt.GetString() : null;
            if (!string.IsNullOrEmpty(variantTitle) && variantTitle != "Default Title")
                title = $"{title} - {variantTitle}";
            return new PageResult
            {
                Price = price,
                InStock = inStock,
                Title = title,
                VariantId = chosen.GetProperty("id").ToString(),
                Notes = ["shopify product json"],
            };
        }
    }

    public sealed record Selectors(string? Price, string? Title, string? InStock, string? OutOfStockText);

    /// <summary>A page's HTML: structured data (JSON-LD, then meta tags), with the site's own selectors winning where set.</summary>
    public static PageResult ParseHtml(string html, Selectors selectors)
    {
        using var document = Html.ParseDocument(html);
        var notes = new List<string>();
        decimal? price = null;
        bool? inStock = null;
        string? title = null;
        string? currency = null;

        // 1. JSON-LD Product.
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var product in FindProducts(json.RootElement))
                {
                    title ??= Str(product, "name");
                    var (p, stock, cur) = ReadOffers(product);
                    if (p != null && price == null)
                    {
                        price = p;
                        notes.Add("price: json-ld");
                    }
                    if (stock != null && inStock == null)
                    {
                        inStock = stock;
                        notes.Add("stock: json-ld");
                    }
                    currency ??= cur;
                }
            }
            catch (JsonException)
            {
                // Broken JSON-LD is common; the rest of the page may still have it.
            }
        }

        // 2. Meta tags / microdata.
        if (price == null)
        {
            var meta = Meta(document, "product:price:amount") ?? Meta(document, "og:price:amount")
                ?? document.QuerySelector("[itemprop='price']")?.GetAttribute("content")
                ?? document.QuerySelector("[itemprop='price']")?.TextContent;
            if (ParsePrice(meta) is { } p)
            {
                price = p;
                notes.Add("price: meta");
            }
        }
        currency ??= Meta(document, "product:price:currency") ?? Meta(document, "og:price:currency")
            ?? document.QuerySelector("[itemprop='priceCurrency']")?.GetAttribute("content");
        if (inStock == null)
        {
            var availability = Meta(document, "product:availability") ?? Meta(document, "og:availability")
                ?? document.QuerySelector("[itemprop='availability']")?.GetAttribute("href")
                ?? document.QuerySelector("[itemprop='availability']")?.GetAttribute("content");
            if (Availability(availability) is { } s)
            {
                inStock = s;
                notes.Add("stock: meta");
            }
        }
        title ??= Meta(document, "og:title") ?? document.QuerySelector("h1")?.TextContent ?? document.Title;

        // 3. The site's selectors win.
        if (!string.IsNullOrWhiteSpace(selectors.Price))
        {
            var el = Query(document, selectors.Price);
            var text = el?.GetAttribute("content") ?? el?.TextContent;
            if (ParsePrice(text) is { } p)
            {
                price = p;
                notes.Add("price: selector");
            }
            else
                notes.Add(el == null ? "price selector matched nothing" : $"price selector text has no price: \"{Trim(text, 60)}\"");
        }
        if (!string.IsNullOrWhiteSpace(selectors.Title) && Query(document, selectors.Title) is { } titleEl)
            title = titleEl.TextContent;
        if (!string.IsNullOrWhiteSpace(selectors.InStock))
        {
            var el = Query(document, selectors.InStock);
            inStock = el != null && !el.HasAttribute("disabled") && el.GetAttribute("aria-disabled") != "true";
            notes.Add(inStock == true ? "stock: in-stock selector found" : "stock: in-stock selector missing or disabled");
        }
        if (!string.IsNullOrWhiteSpace(selectors.OutOfStockText))
        {
            var body = document.Body?.TextContent ?? "";
            var hit = selectors.OutOfStockText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(w => body.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                inStock = false;
                notes.Add($"stock: page says \"{hit}\"");
            }
        }

        if (price == null)
            notes.Add("no price found");
        if (inStock == null)
            notes.Add("stock unknown");
        return new PageResult { Price = price, InStock = inStock, Title = Clean(title), Currency = currency, Notes = notes };
    }

    private static IElement? Query(IDocument document, string selector)
    {
        try
        {
            return document.QuerySelector(selector.Trim());
        }
        catch (Exception)
        {
            throw new InvalidOperationException($"Not a valid CSS selector: {selector}");
        }
    }

    private static string? Meta(IDocument d, string property) =>
        (d.QuerySelector($"meta[property='{property}']") ?? d.QuerySelector($"meta[name='{property}']"))?.GetAttribute("content");

    private static IEnumerable<JsonElement> FindProducts(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                foreach (var p in FindProducts(item))
                    yield return p;
                break;
            case JsonValueKind.Object:
                if (IsType(e, "Product") || IsType(e, "ProductGroup"))
                    yield return e;
                if (e.TryGetProperty("@graph", out var graph))
                    foreach (var p in FindProducts(graph))
                        yield return p;
                if (e.TryGetProperty("hasVariant", out var variants))
                    foreach (var p in FindProducts(variants))
                        yield return p;
                if (e.TryGetProperty("mainEntity", out var main))
                    foreach (var p in FindProducts(main))
                        yield return p;
                break;
        }
    }

    private static bool IsType(JsonElement e, string type) =>
        e.TryGetProperty("@type", out var t) && (t.ValueKind == JsonValueKind.String && t.GetString() == type
            || t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.GetString() == type));

    private static (decimal? Price, bool? InStock, string? Currency) ReadOffers(JsonElement product)
    {
        if (!product.TryGetProperty("offers", out var offers))
            return (null, null, null);
        var list = offers.ValueKind == JsonValueKind.Array ? offers.EnumerateArray().ToList() : [offers];
        decimal? best = null;
        bool? anyInStock = null;
        string? currency = null;
        foreach (var offer in list)
        {
            if (offer.ValueKind != JsonValueKind.Object)
                continue;
            // AggregateOffer nests its offers.
            if (offer.TryGetProperty("offers", out _))
            {
                var (p, s, c) = ReadOffers(offer);
                Merge(p, s, c);
            }
            var price = ParsePrice(Str(offer, "price")) ?? ParsePrice(Str(offer, "lowPrice"));
            if (price == null && offer.TryGetProperty("priceSpecification", out var spec))
                price = ParsePrice(Str(spec.ValueKind == JsonValueKind.Array && spec.GetArrayLength() > 0 ? spec[0] : spec, "price"));
            Merge(price, Availability(Str(offer, "availability")), Str(offer, "priceCurrency"));
        }
        return (best, anyInStock, currency);

        void Merge(decimal? price, bool? stock, string? cur)
        {
            if (price != null && (best == null || price < best))
                best = price;
            if (stock != null)
                anyInStock = (anyInStock ?? false) || stock.Value;
            currency ??= cur;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.Object => v.TryGetProperty("@id", out var id) ? id.GetString() : null,
                _ => null,
            }
            : null;

    /// <summary>schema.org availability (a URL or a bare word) or a meta tag's "in stock".</summary>
    public static bool? Availability(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
        var last = v[(v.LastIndexOf('/') + 1)..];
        return last switch
        {
            "instock" or "instoreonly" or "onlineonly" or "limitedavailability" or "preorder" or "presale" or "available" => true,
            "outofstock" or "soldout" or "discontinued" or "backorder" or "preordernotavailable" or "oos" or "unavailable" => false,
            _ => null,
        };
    }

    /// <summary>The first amount in the text: "$1,049.99", "1 049,99 €", "CA$899" and the like.</summary>
    public static decimal? ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = Number().Match(text.Replace(' ', ' ').Replace(' ', ' '));
        if (!m.Success)
            return null;
        var s = m.Value.Replace(" ", "").Replace("'", "");
        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            // Whichever comes last is the decimal point.
            s = lastDot > lastComma ? s.Replace(",", "") : s.Replace(".", "").Replace(',', '.');
        }
        else if (lastComma >= 0)
        {
            // "899,99" is a decimal comma; "1,049" is thousands (and "1.049" below).
            s = s.Length - lastComma - 1 == 2 && s.Count(c => c == ',') == 1 ? s.Replace(',', '.') : s.Replace(",", "");
        }
        else if (lastDot >= 0 && (s.Count(c => c == '.') > 1 || s.Length - lastDot - 1 == 3))
        {
            s = s.Replace(".", "");
        }
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length > 300 ? s[..300] : s;
    }

    private static string Trim(string? s, int n) => s == null ? "" : (s.Length > n ? s[..n] + "…" : s).Trim();

    [GeneratedRegex(@"\d{1,3}(?:[ ,.']\d{3})+(?:[.,]\d{1,2})?(?!\d)|\d+(?:[.,]\d{1,2})?")]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

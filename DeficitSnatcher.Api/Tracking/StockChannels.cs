using System.Text.Json;
using AngleSharp.Html.Parser;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// How to read one way of getting the product off a page (online delivery, or pickup at a store): the element that shows
/// its status, the words it shows when it can be had and when it can't, and (pickup only, in the browser) a button to
/// click first when the page only shows pickup after choosing it.
/// </summary>
public sealed record ChannelRule(string Container, string? InStockText, string? OutOfStockText, string? Click = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Container);
}

/// <summary>A page's online and pickup rules (either may be missing).</summary>
public sealed record ChannelRules(ChannelRule? Online, ChannelRule? Pickup)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => (Online?.IsEmpty ?? true) && (Pickup?.IsEmpty ?? true);
}

/// <summary>
/// A page counts as in stock when it can be bought online or picked up nearby: both are read, separately, and the verdict
/// says which. The rules are the page's own (learned by the AI's set-up, or typed in), else built in for stores known here.
/// </summary>
public static class StockChannels
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Built-in rules for stores whose pages show both (seen on their pages).</summary>
    private static readonly (string Domain, ChannelRules Rules)[] BuiltIn =
    [
        // Canada Computers: a "Delivery" box (#onlineinfo: "Sold out online") and a store box (#storeinfo: "Sold Out at All
        // Locations"), both in the page itself.
        ("canadacomputers.com", new ChannelRules(
            new ChannelRule("#onlineinfo", "available, in stock, ships, delivery by, get it", "sold out, not available, unavailable, coming soon, back order"),
            new ChannelRule("#storeinfo", "available, in stock, pick up, pickup, ready", "sold out, not available, unavailable, coming soon"))),
        // Staples: a Shopify store whose product data says "available" while the page says Out of Stock. Its delivery box and
        // its store-pickup box (after choosing a store) are drawn by the page's scripts, so it's read in the browser.
        ("staples.ca", new ChannelRules(
            new ChannelRule("div.switch-to-get-in-stock__delivery", "Delivers by, available", "Delivery currently unavailable"),
            new ChannelRule("div.product-bopis-accordion__content.product-bopis-accordion__content--open", "Pickup in store",
                "0 in stock, Out of stock at your store", "a.change_store"))),
    ];

    /// <summary>Stores whose stock only shows on the drawn page: never read from their hidden product data (Shopify JSON) or a plain
    /// request, always in the browser.</summary>
    public static bool BrowserOnly(Uri url) => url.Host.EndsWith("staples.ca", StringComparison.OrdinalIgnoreCase);

    public static ChannelRules? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var rules = JsonSerializer.Deserialize<ChannelRules>(json, Json);
            return rules == null || rules.IsEmpty ? null : rules;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Serialize(ChannelRules? rules) =>
        rules == null || rules.IsEmpty ? null : JsonSerializer.Serialize(Clean(rules), Json);

    private static ChannelRules Clean(ChannelRules r) => new(
        r.Online is { IsEmpty: false } o ? o with { Click = null } : null,
        r.Pickup is { IsEmpty: false } p ? p : null);

    /// <summary>The page's own rules, else the built-in ones for its store, else none.</summary>
    public static ChannelRules? For(string? json, Uri url) =>
        Parse(json) ?? BuiltIn.FirstOrDefault(b => url.Host.EndsWith(b.Domain, StringComparison.OrdinalIgnoreCase)).Rules;

    private static readonly HtmlParser Html = new();

    /// <summary>The result with stock from online and pickup (pickup read from <paramref name="pickupHtml"/> when the page had
    /// to be clicked to show it). Unchanged when neither could be read.</summary>
    public static PageResult Apply(PageResult result, string html, string? pickupHtml, ChannelRules? rules)
    {
        if (rules == null)
            return result;
        var online = rules.Online is { IsEmpty: false } o ? Read(html, o) : null;
        var pickup = rules.Pickup is { IsEmpty: false } p ? Read(pickupHtml ?? html, p) : null;
        if (online == null && pickup == null)
        {
            // The page didn't show its delivery / pickup boxes (still drawing, or a different layout): its hidden product data
            // alone isn't trusted to say "in stock" (Staples' said so while the page said Out of Stock). Out of stock stands.
            return result.InStock == true
                ? result with
                {
                    InStock = null,
                    StockDetail = "online and pickup not shown on the page",
                    Notes = [.. result.Notes.Where(n => !n.StartsWith("stock:")), "stock: the page's data said in stock, but its online/pickup boxes weren't found; not trusted"],
                }
                : result with { Notes = [.. result.Notes, "online/pickup: their elements weren't found on the page"] };
        }
        bool? inStock = online == true || pickup == true ? true : online == false || pickup == false ? false : null;
        var detail = $"online: {Word(online, rules.Online)} · pickup: {Word(pickup, rules.Pickup)}";
        var notes = result.Notes.Where(n => !n.StartsWith("stock:") && n != "stock unknown").ToList();
        notes.Add($"stock: {detail}");
        return result with { InStock = inStock ?? result.InStock, StockDetail = detail, Notes = notes };
    }

    private static string Word(bool? value, ChannelRule? rule) =>
        rule is null or { IsEmpty: true } ? "not set up" : value switch { true => "yes", false => "no", null => "?" };

    /// <summary>One channel: sold-out words win; then available words; an element with neither is unknown.</summary>
    private static bool? Read(string html, ChannelRule rule)
    {
        using var document = Html.ParseDocument(html);
        string text;
        try
        {
            var elements = document.QuerySelectorAll(rule.Container);
            if (elements.Length == 0)
                return null;
            text = string.Join(" ", elements.Select(e => e.TextContent));
        }
        catch (AngleSharp.Dom.DomException)
        {
            return null;
        }
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        // Sold-out words win, and the usual ones always count, whatever words the rule has: an AI-picked rule without "Out of
        // Stock" read Staples' box as available from another word in it while the box said Out of Stock.
        if (Words(rule.OutOfStockText).Concat(AlwaysSoldOut).Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (Words(rule.InStockText).Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return true;
        return null;
    }

    private static readonly string[] AlwaysSoldOut =
        ["out of stock", "sold out", "unavailable", "not available", "no longer available", "coming soon", "back order", "backorder"];

    private static IEnumerable<string> Words(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

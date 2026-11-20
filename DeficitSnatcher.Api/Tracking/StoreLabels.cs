using AngleSharp.Html.Parser;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Built-in ways to read stock and price on stores whose pages have no structured data to go on, taken from StreetMerchant's
/// store models (github.com/jef/streetmerchant, src/store/model): an element and the words it holds when in stock, or out of
/// stock. Only used for what the page's own data and the site's selectors left unknown.
/// </summary>
public static class StoreLabels
{
    private sealed record Label(string Container, string[] Text);

    private sealed record Store(string Domain, Label[] InStock, Label[] OutOfStock, string[] Price);

    private static readonly Store[] Stores =
    [
        new("amazon.ca",
            [new("#desktop_buybox", ["add to cart", "buy now"]), new("#add-to-cart-button", [""])],
            [new(".a-color-price", ["currently unavailable"]), new("#availability", ["currently unavailable", "out of stock"])],
            ["#corePrice_feature_div .a-offscreen", ".a-price .a-offscreen", ".a-color-price"]),
        new("bestbuy.ca",
            [new(".addToCartButton:not(:disabled)", ["add to cart"]), new("button.add-to-cart:not(:disabled)", ["add to cart"])],
            [new(".addToCartButton:disabled", ["add to cart", "sold out", "coming soon"])],
            ["div[class*='pricingContainer']", "[data-automation='product-price']"]),
        new("walmart.ca",
            [new("[data-automation='cta-button']", ["add to cart"]), new("[data-automation-id='atc']", ["add to cart"])],
            [new("[data-automation='cta-button'][disabled]", [""])],
            ["[data-automation='buybox-price']", "[itemprop='price']"]),
        new("canadacomputers.com",
            [new("div[class='pi-prod-availability']", ["online in stock"]), new(".pi-prod-availability", ["online in stock"])],
            [new(".pi-prod-availability", ["not available online", "sold out"])],
            [".h2-big > strong:nth-child(1)"]),
    ];

    private static readonly HtmlParser Html = new();

    public static PageResult Apply(PageResult result, string html, Uri url)
    {
        var store = Stores.FirstOrDefault(s => url.Host.EndsWith(s.Domain, StringComparison.OrdinalIgnoreCase));
        if (store == null || result.InStock != null && result.Price != null)
            return result;
        using var document = Html.ParseDocument(html);
        var notes = result.Notes.ToList();
        var inStock = result.InStock;
        var price = result.Price;
        if (inStock == null)
        {
            if (Matches(document, store.OutOfStock))
                inStock = false;
            else if (Matches(document, store.InStock))
                inStock = true;
            if (inStock != null)
                notes.RemoveAll(n => n == "stock unknown");
            if (inStock != null)
                notes.Add($"stock: built-in {store.Domain} label");
        }
        if (price == null)
            foreach (var selector in store.Price)
            {
                try
                {
                    if (PageParser.ParsePrice(document.QuerySelector(selector)?.TextContent) is { } p)
                    {
                        price = p;
                        notes.RemoveAll(n => n == "no price found");
                        notes.Add($"price: built-in {store.Domain} label");
                        break;
                    }
                }
                catch (AngleSharp.Dom.DomException)
                {
                }
            }
        return result with { InStock = inStock, Price = price, Notes = notes };
    }

    private static bool Matches(AngleSharp.Html.Dom.IHtmlDocument document, Label[] labels)
    {
        foreach (var label in labels)
        {
            try
            {
                foreach (var el in document.QuerySelectorAll(label.Container))
                {
                    var text = el.TextContent ?? "";
                    if (label.Text.Any(t => t.Length == 0 || text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch (AngleSharp.Dom.DomException)
            {
                // A selector this parser can't read: skip it.
            }
        }
        return false;
    }
}

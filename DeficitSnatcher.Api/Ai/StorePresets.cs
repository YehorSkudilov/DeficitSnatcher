using DeficitSnatcher.Api.Data;

namespace DeficitSnatcher.Api.Ai;

/// <summary>
/// Stores whose product pages always look the same: their settings are built in, tried before asking the AI.
/// </summary>
public static class StorePresets
{
    public sealed record Preset(string Name, SiteKind Kind, string? Price, string? Title, string? InStock, string? OutOfStockText, string Notes);

    public static Preset? For(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.Contains(".amazon.") || host.StartsWith("amazon."))
            return new Preset(
                host.EndsWith(".ca") ? "Amazon.ca" : host.EndsWith(".com") ? "Amazon" : "Amazon",
                SiteKind.Html,
                // The buy box's current price (not the crossed-out list price), in its current and older layouts.
                "#corePrice_feature_div .a-price:not(.a-text-price) .a-offscreen, #corePriceDisplay_desktop_feature_div .priceToPay .a-offscreen, "
                + "#apex_desktop .a-price:not(.a-text-price) .a-offscreen, #price_inside_buybox, #newBuyBoxPrice, #priceblock_ourprice",
                "#productTitle",
                "#add-to-cart-button, #buy-now-button",
                // No buy box: only other sellers (often marked up), or none.
                "Currently unavailable, See All Buying Options, Temporarily out of stock",
                "Amazon's built-in settings: the buy box's price, in stock while Add to Cart is there. When only other sellers offer it "
                + "(\"See All Buying Options\") it counts as out of stock, so marked-up resellers aren't bought.");
        return null;
    }
}

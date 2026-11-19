using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Tracking;

namespace DeficitSnatcher.Api.Buying;

/// <summary>Whether what a check found is a deal: in stock, the title is the right thing, and the price is in range.</summary>
public static class BuyRules
{
    public sealed record Verdict(bool Match, decimal? TotalPrice, string Reason);

    public static Verdict Evaluate(Product product, Site site, PageResult result)
    {
        if (result.InStock != true)
            return new(false, null, result.InStock == false ? "Out of stock" : "Stock unknown");
        if (result.Price is not { } price)
            return new(false, null, "No price found");
        var total = price + site.ShippingCost;
        if (!TitleMatches(product, result.Title, out var why))
            return new(false, total, why);
        if (total < product.MinPrice)
            return new(false, total, $"{total:0.00} is under the minimum {product.MinPrice:0.00} (likely not the real thing)");
        if (total > product.MaxPrice)
            return new(false, total, $"{total:0.00} is over the maximum {product.MaxPrice:0.00}");
        return new(true, total, $"In stock at {total:0.00}, in range {product.MinPrice:0.00}–{product.MaxPrice:0.00}");
    }

    public static bool TitleMatches(Product product, string? title, out string why)
    {
        var t = title ?? "";
        // Each comma-separated group must be there; "a|b" is either.
        foreach (var group in Words(product.RequiredKeywords))
            if (!group.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(word => t.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                why = $"Title doesn't contain \"{group.Replace("|", "\" or \"")}\"";
                return false;
            }
        foreach (var word in Words(product.ExcludedKeywords))
            if (t.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                why = $"Title contains excluded \"{word}\"";
                return false;
            }
        why = "";
        return true;
    }

    private static IEnumerable<string> Words(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

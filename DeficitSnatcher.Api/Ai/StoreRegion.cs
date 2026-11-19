using System.Text.RegularExpressions;

namespace DeficitSnatcher.Api.Ai;

/// <summary>
/// Whether a store page is for the owner's country (from the checkout profile's country, or the currency): adding pages
/// leaves out stores of other countries, e.g. amazon.com or a /en-us/ page for someone in Canada.
/// </summary>
public static partial class StoreRegion
{
    public sealed record Country(string Code, string Name, string Currency, string[] Tlds);

    private static readonly Country[] Countries =
    [
        new("ca", "Canada", "CAD", [".ca"]),
        new("us", "the United States", "USD", [".us"]),
        new("uk", "the United Kingdom", "GBP", [".uk"]),
        new("au", "Australia", "AUD", [".au"]),
        new("nz", "New Zealand", "NZD", [".nz"]),
    ];

    /// <summary>The owner's country: the profile's country if it's set and known, else the currency's.</summary>
    public static Country? For(string? profileCountry, string currency)
    {
        var c = (profileCountry ?? "").Trim().ToLowerInvariant();
        var byName = c switch
        {
            "ca" or "can" or "canada" => "ca",
            "us" or "usa" or "united states" or "united states of america" => "us",
            "uk" or "gb" or "united kingdom" or "great britain" or "england" => "uk",
            "au" or "australia" => "au",
            "nz" or "new zealand" => "nz",
            _ => null,
        };
        return Countries.FirstOrDefault(x => x.Code == byName) ?? Countries.FirstOrDefault(x => x.Currency == currency);
    }

    // Chains with a store per country: their .com is the US store.
    private static readonly HashSet<string> NationalChains =
    [
        "amazon.com", "bestbuy.com", "walmart.com", "costco.com", "gamestop.com", "newegg.com", "staples.com", "toysrus.com",
        "target.com", "ebay.com",
    ];

    // Two-letter endings that are used worldwide, not a country's.
    private static readonly HashSet<string> GenericTwoLetter = ["co", "io", "tv", "me", "ai", "gg", "fm", "ly", "to", "cc"];

    /// <summary>Why the page is for another country, or null when it may well be for the owner's.</summary>
    public static string? Foreign(string url, string domain, Country? home)
    {
        if (home == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var host = uri.Host.ToLowerInvariant();
        var tld = host[(host.LastIndexOf('.') + 1)..];

        // Another country's domain (bestbuy.com is checked below; .ca for a US owner, .co.uk for a Canadian…).
        if (tld.Length == 2 && !GenericTwoLetter.Contains(tld) && !home.Tlds.Any(t => host.EndsWith(t)))
            return $"{domain} is a store for another country (.{tld}).";

        // The US store of a chain that has one for the owner's country.
        if (home.Code != "us" && NationalChains.Contains(domain))
            return $"{domain} is the US store; use the {home.Name} one.";

        // A page of a worldwide site for another country: /en-us/, ?locale=en_US…
        var path = (uri.AbsolutePath + uri.Query).ToLowerInvariant();
        foreach (Match m in Locale().Matches(path))
        {
            var region = m.Groups[1].Value;
            var mine = home.Code == "uk" ? "gb" : home.Code;
            if (region != mine && !(region == "uk" && mine == "gb"))
                return $"The page is for another country ({m.Value.Trim('/', '?', '&', '=')}).";
        }
        return null;
    }

    /// <summary>A page whose price is in another currency is for another country.</summary>
    public static string? ForeignCurrency(string? pageCurrency, string currency) =>
        pageCurrency is { Length: 3 } c && !string.Equals(c, currency, StringComparison.OrdinalIgnoreCase)
            ? $"Prices are in {c.ToUpperInvariant()}, not {currency}: a store for another country."
            : null;

    [GeneratedRegex(@"(?:^|[/=_-])(?:en|fr|es|de)[-_](us|ca|gb|uk|au|nz)(?=$|[/&?_-])")]
    private static partial Regex Locale();
}

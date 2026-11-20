namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Recognises a store's bot-protection page (Akamai's "Access Denied", Cloudflare's challenge, HUMAN/PerimeterX's "Press &amp;
/// Hold", DataDome, Imperva), so a blocked check says so instead of reading as "no price found".
/// </summary>
public static class BlockDetector
{
    public const string Advice =
        "The store refused this request. Let its cooldown expire, check the product URL, and use a longer interval. "
        + "A normal signed-in browser session may help when the store allows it.";

    private static readonly (string Name, string[] Signs)[] Vendors =
    [
        ("Akamai", ["Access Denied</title>", "You don't have permission to access", "errors.edgesuite.net", "Reference&#32;&#35;"]),
        ("Cloudflare", ["<title>Just a moment...</title>", "cf-chl-", "challenge-platform", "Attention Required! | Cloudflare"]),
        ("HUMAN (PerimeterX)", ["px-captcha", "Press &amp; Hold", "Press & Hold", "_pxCaptcha"]),
        ("DataDome", ["captcha-delivery.com", "geo.captcha-delivery", "datadome"]),
        ("Imperva", ["Pardon Our Interruption", "_Incapsula_Resource", "incap_ses"]),
        ("Amazon's robot check", ["/errors/validateCaptcha", "Enter the characters you see below", "Sorry, we just need to make sure you're not a robot"]),
    ];

    /// <summary>The bot protection that answered, or null for a normal page. Only short pages count: a real product page
    /// can mention these words in its scripts.</summary>
    public static string? Detect(string? html, int status = 200)
    {
        if (string.IsNullOrEmpty(html))
            return status is 403 or 429 ? "the store's bot protection" : null;
        var head = html.Length > 60_000 && status is not (403 or 429 or 503) ? null : html;
        if (head == null)
            return null;
        foreach (var (name, signs) in Vendors)
            if (signs.Any(s => head.Contains(s, StringComparison.OrdinalIgnoreCase)))
                return name;
        if (status is 403 or 429 || head.Contains("<title>Access Denied", StringComparison.OrdinalIgnoreCase))
            return "the store's bot protection";
        return null;
    }

    public static string Message(string vendor, bool browser) =>
        $"Blocked by {vendor}{(browser ? " (even in the browser)" : "")}. {Advice}";
}

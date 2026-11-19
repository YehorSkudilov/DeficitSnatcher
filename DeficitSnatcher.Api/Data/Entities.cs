namespace DeficitSnatcher.Api.Data;

/// <summary>
/// Something to buy (e.g. "PS5 Pro"), with the price range it's bought in and how many to buy in all. Every site that
/// sells it is a <see cref="Site"/> under it.
/// </summary>
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Lowest price that's believable: anything under it is taken as a wrong listing (accessory, scam, parse
    /// error) and never bought.</summary>
    public decimal MinPrice { get; set; }

    /// <summary>Highest price (including the site's shipping) it's bought at.</summary>
    public decimal MaxPrice { get; set; }

    /// <summary>Units to buy in all, over every site. Auto-buy stops once this many were bought.</summary>
    public int MaxQuantity { get; set; } = 1;

    /// <summary>Units per order.</summary>
    public int QuantityPerOrder { get; set; } = 1;

    /// <summary>Comma-separated words the listing's title must all contain (case-insensitive; "a|b": either); empty: any
    /// title.</summary>
    public string RequiredKeywords { get; set; } = "";

    /// <summary>Comma-separated words that rule a listing out (e.g. "refurbished, controller only").</summary>
    public string ExcludedKeywords { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public List<Site> Sites { get; set; } = [];
}

public enum SiteKind
{
    /// <summary>Tries Shopify's product JSON, then the page's structured data (JSON-LD, meta tags), then selectors.</summary>
    Auto,
    /// <summary>A Shopify store's product page (reads {url}.js: exact price and stock per variant).</summary>
    Shopify,
    /// <summary>The page's HTML, read with the site's selectors / structured data.</summary>
    Html,
    /// <summary>Like Html, but the page is rendered in headless Chromium first (sites that build the page in script or
    /// block plain HTTP clients).</summary>
    Browser,
}

/// <summary>A product page to watch on one site.</summary>
public class Site
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public SiteKind Kind { get; set; } = SiteKind.Auto;
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between checks; null: the default from settings.</summary>
    public int? IntervalSeconds { get; set; }

    /// <summary>Added to the listed price before it's compared with the product's range.</summary>
    public decimal ShippingCost { get; set; }

    // Optional CSS selectors for pages without usable structured data.
    public string? PriceSelector { get; set; }
    public string? TitleSelector { get; set; }
    /// <summary>An element that only exists while it can be bought (e.g. an enabled "Add to cart" button).</summary>
    public string? InStockSelector { get; set; }
    /// <summary>Text on the page that means it can't be bought (e.g. "Sold out"), comma-separated.</summary>
    public string? OutOfStockText { get; set; }
    /// <summary>Shopify: only this variant (id) is looked at; null: the first available one.</summary>
    public string? VariantId { get; set; }

    /// <summary>Buy automatically when it's in stock in the product's range (and auto-buy is armed in settings).</summary>
    public bool AutoBuy { get; set; }

    /// <summary>The checkout script, JSON array of <see cref="Buying.CheckoutStep"/>.</summary>
    public string CheckoutSteps { get; set; } = "[]";

    /// <summary>Cookies for the site (a signed-in session exported from a browser), encrypted (see SecretBox).</summary>
    public string? CookiesEncrypted { get; set; }

    // What the last check found.
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastChangedAt { get; set; }
    public decimal? LastPrice { get; set; }
    public bool? LastInStock { get; set; }
    public string? LastTitle { get; set; }
    /// <summary>Shopify: the variant the last check looked at ({{variantId}} in the checkout steps).</summary>
    public string? LastVariantId { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveErrors { get; set; }
    public DateTimeOffset? LastBuyAttemptAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A change seen on a site (price or stock), for its price history. Only changes are kept.</summary>
public class PricePoint
{
    public long Id { get; set; }
    public int SiteId { get; set; }
    public DateTimeOffset At { get; set; }
    public decimal? Price { get; set; }
    public bool InStock { get; set; }
}

public enum PurchaseStatus
{
    Running,
    /// <summary>The checkout ran up to (not including) the final step: dry-run mode.</summary>
    DryRun,
    Succeeded,
    Failed,
    /// <summary>A real checkout cut off by a server restart: it may have gone through, so it counts against the limits
    /// until it's marked as not bought.</summary>
    Interrupted,
}

public class Purchase
{
    public int Id { get; set; }
    public int SiteId { get; set; }
    public int ProductId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public PurchaseStatus Status { get; set; }
    public string Message { get; set; } = "";
    public bool DryRun { get; set; }
    /// <summary>Started from the web (Buy now / Dry-run checkout) rather than by a check.</summary>
    public bool Manual { get; set; }
    /// <summary>Why it ran: what the check saw and the limits at the time.</summary>
    public string Context { get; set; } = "";
    /// <summary>Each step as it ran (JSON array of Buying.StepRecord).</summary>
    public string Steps { get; set; } = "[]";
    /// <summary>Plain-text log of the whole checkout.</summary>
    public string Log { get; set; } = "";
    /// <summary>The full error of the step that failed (Playwright's call log included).</summary>
    public string? Error { get; set; }
    /// <summary>Browser console errors, failed requests and error responses during the checkout.</summary>
    public string BrowserLog { get; set; } = "";
    /// <summary>The page's HTML where the checkout ended (failed or not).</summary>
    public string? PageHtml { get; set; }
    /// <summary>Where it ended, without the query (which can hold what was typed).</summary>
    public string? FinalUrl { get; set; }
}

/// <summary>A screenshot taken during a checkout: after a step, or of the whole page where it ended.</summary>
public class PurchaseShot
{
    public long Id { get; set; }
    public int PurchaseId { get; set; }
    /// <summary>The step (0-based) it was taken after; null: the final full-page one.</summary>
    public int? StepIndex { get; set; }
    public string Label { get; set; } = "";
    public string ContentType { get; set; } = "image/jpeg";
    public byte[] Data { get; set; } = [];
    public DateTimeOffset At { get; set; }
}

public enum ScanOutcome
{
    InStock,
    OutOfStock,
    /// <summary>The page was read but didn't say whether it can be bought.</summary>
    Unknown,
    /// <summary>The page couldn't be fetched or read.</summary>
    Error,
}

/// <summary>One check of one site, kept whatever it found (the Scans page).</summary>
public class Scan
{
    public long Id { get; set; }
    public int SiteId { get; set; }
    public int ProductId { get; set; }
    public DateTimeOffset At { get; set; }
    public int DurationMs { get; set; }
    public ScanOutcome Outcome { get; set; }
    public decimal? Price { get; set; }
    /// <summary>Price plus the site's shipping: what's compared with the range.</summary>
    public decimal? TotalPrice { get; set; }
    public string? Title { get; set; }
    /// <summary>In stock, the right title, in range.</summary>
    public bool Match { get; set; }
    /// <summary>Why it is or isn't a match.</summary>
    public string Verdict { get; set; } = "";
    public string? Error { get; set; }
    /// <summary>Where the values came from on the page.</summary>
    public string? Notes { get; set; }
    /// <summary>What was done about it: a checkout started, or why not.</summary>
    public string? Action { get; set; }
    public int? PurchaseId { get; set; }
    /// <summary>Started from the web (Check now) rather than on schedule.</summary>
    public bool Manual { get; set; }
}

public enum EventLevel
{
    Info,
    Good,
    Warning,
    Error,
}

/// <summary>The activity log.</summary>
public class Event
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public EventLevel Level { get; set; }
    public int? SiteId { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>Key/value store: the settings and the checkout profile, as JSON (see Settings/).</summary>
public class StoredValue
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

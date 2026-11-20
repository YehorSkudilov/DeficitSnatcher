namespace DeficitSnatcher.Api.Settings;

/// <summary>
/// The settings. Only a few can be changed (the Settings page: auto-buy, how often pages are checked, email alerts); the
/// rest are fixed here, with what works best. Kept encrypted in the database (see SettingsStore). Properties without a
/// setter aren't saved or read back: they're always these values.
/// </summary>
public sealed record BotSettings
{
    /// <summary>The master switch: nothing is bought, on any site, while it's off.</summary>
    public bool AutoBuyArmed { get; init; }

    /// <summary>How often each page is checked, in seconds (at least a minute: checking more often gets you flagged).</summary>
    public int DefaultIntervalSeconds { get; init; } = 180;

    // Email alerts (SMTP), IONOS's settings by default: on once the address to send to, the username and password are set.
    /// <summary>Where alerts are emailed (several: comma-separated).</summary>
    public string EmailTo { get; init; } = "";
    /// <summary>IONOS: smtp.ionos.com (North America), smtp.ionos.co.uk, smtp.ionos.de…</summary>
    public string SmtpHost { get; init; } = "smtp.ionos.com";
    /// <summary>587 (STARTTLS), or 465 (TLS from the start).</summary>
    public int SmtpPort { get; init; } = 587;
    /// <summary>IONOS: the mailbox's full email address.</summary>
    public string SmtpUser { get; init; } = "";
    public string SmtpPassword { get; init; } = "";
    /// <summary>Sent from; empty: the SMTP username.</summary>
    public string EmailFrom { get; init; } = "";

    // Kept as saved, not on the page.
    public string Currency { get; init; } = "CAD";
    /// <summary>For restock days and times (IANA, e.g. America/Toronto).</summary>
    public string TimeZone { get; init; } = "America/Toronto";

    // ---- Fixed ----

    /// <summary>Checkouts place the order (there's no dry run).</summary>
    public bool DryRun => false;
    /// <summary>No total limit beyond each product's price range and units.</summary>
    public decimal TotalBudget => 0;
    /// <summary>After a failed checkout, the same site isn't tried again for this long.</summary>
    public int BuyRetryMinutes => 10;
    public bool AiRestockForecast => true;
    public bool TrackingEnabled => true;
    /// <summary>Each wait is randomly up to this much longer or shorter, so checks don't land on a fixed beat.</summary>
    public int JitterPercent => 25;
    public bool SlowDownFailingPages => false;
    /// <summary>Two checks of the same store are at least this far apart.</summary>
    public int MinSecondsPerHost => 20;
    public List<StoreInterval> StoreIntervals => [];
    public int MaxConcurrentChecks => 3;
    public int RequestTimeoutSeconds => 30;
    public string ProxyUrl => "";
    /// <summary>The Camoufox Playwright server (DeficitSnatcher.Camoufox) in Docker.</summary>
    public string CamoufoxUrl => DefaultCamoufoxUrl;
    public const string DefaultCamoufoxUrl = "ws://camoufox:4444/camoufox";
    /// <summary>The server's browser: Camoufox (Chromium when it can't be reached).</summary>
    public string AppBrowser => "Camoufox";
    /// <summary>A page's browser session (cookies, local storage) is carried from check to check and into checkouts for
    /// this long, then started afresh.</summary>
    public int BrowserSessionMinutes => 180;
    public string UserAgent =>
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    public int HistoryDays => 90;
    public int ScanHistoryDays => 30;
    public int CheckoutHistoryDays => 0;
    public bool StepScreenshots => true;
    public string ScanSnapshots => "Changes";

    // AI (ChatGPT): on while OpenAI__ApiKey is set.
    public bool AiEnabled => true;
    public string AiModel => "gpt-5-mini";
    public string AiReasoningEffort => "low";
    public bool AiCheckoutWithoutSteps => true;
    public bool AiAutofixCheckout => true;
    public bool AiSaveFixedSteps => true;
    public bool AiAutofixReading => true;
    public int AiMaxActions => 30;
    public bool AiSendScreenshots => true;
    /// <summary>Before a real order the order total must be at most the product's maximum (times units) plus this much tax.</summary>
    public int MaxTaxPercent => 15;
    public bool AiCheckOrder => true;
    public int AiDailyTokenLimit => 300_000;
    public int AiCooldownHours => 6;

    // What's emailed: matches, purchases, failed checkouts, failing pages.
    public bool NotifyInStock => true;
    public bool NotifyInStockOutOfRange => false;
    public bool NotifyNotBought => true;
    public bool NotifyDryRun => false;
    public bool NotifyPriceDrop => false;
    public bool NotifyPurchase => true;
    public bool NotifyErrors => true;
    public bool? NotifyPageFailing => true;
    public bool? NotifyPageRecovered => true;
    public bool? NotifyAiProblems => true;
    /// <summary>A site is reported as failing once this many checks in a row failed (and again when it recovers).</summary>
    public int ErrorThreshold => 5;

    public BotSettings Normalized() => this with
    {
        DefaultIntervalSeconds = Math.Clamp(DefaultIntervalSeconds, 60, 24 * 3600),
        Currency = string.IsNullOrWhiteSpace(Currency) ? "CAD" : Currency.Trim().ToUpperInvariant(),
        TimeZone = string.IsNullOrWhiteSpace(TimeZone) ? "America/Toronto" : TimeZone.Trim(),
        EmailTo = EmailTo?.Trim() ?? "",
        SmtpHost = string.IsNullOrWhiteSpace(SmtpHost) ? "smtp.ionos.com" : SmtpHost.Trim(),
        SmtpPort = SmtpPort is > 0 and < 65536 ? SmtpPort : 587,
        SmtpUser = SmtpUser?.Trim() ?? "",
        SmtpPassword = SmtpPassword ?? "",
        EmailFrom = EmailFrom?.Trim() ?? "",
    };
}

/// <summary>Who's buying: filled into the checkout steps as {{email}}, {{card.number}} and so on.</summary>
public sealed record CheckoutProfile
{
    public string Email { get; init; } = "";
    public string Phone { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Address1 { get; init; } = "";
    public string Address2 { get; init; } = "";
    public string City { get; init; } = "";
    public string Province { get; init; } = "";
    public string PostalCode { get; init; } = "";
    public string Country { get; init; } = "";
    public string CardName { get; init; } = "";
    public string CardNumber { get; init; } = "";
    public string CardExpiryMonth { get; init; } = "";
    public string CardExpiryYear { get; init; } = "";
    public string CardCvv { get; init; } = "";

    /// <summary>The values the checkout steps can use, by placeholder name.</summary>
    public Dictionary<string, string> Placeholders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["email"] = Email,
        ["phone"] = Phone,
        ["firstName"] = FirstName,
        ["lastName"] = LastName,
        ["fullName"] = $"{FirstName} {LastName}".Trim(),
        ["address1"] = Address1,
        ["address2"] = Address2,
        ["city"] = City,
        ["province"] = Province,
        ["postalCode"] = PostalCode,
        ["country"] = Country,
        ["card.name"] = CardName,
        ["card.number"] = CardNumber,
        ["card.expiryMonth"] = CardExpiryMonth,
        ["card.expiryYear"] = CardExpiryYear,
        ["card.expiryYear2"] = CardExpiryYear.Length >= 2 ? CardExpiryYear[^2..] : CardExpiryYear,
        ["card.expiry"] = $"{CardExpiryMonth}/{(CardExpiryYear.Length >= 2 ? CardExpiryYear[^2..] : CardExpiryYear)}",
        ["card.cvv"] = CardCvv,
    };
}

/// <summary>A store (domain, e.g. walmart.ca) whose pages are checked at most every <see cref="Minutes"/>.</summary>
public sealed record StoreInterval(string Domain, int Minutes)
{
    public static string Clean(string? domain)
    {
        var d = (domain ?? "").Trim().ToLowerInvariant();
        if (Uri.TryCreate(d.Contains("://") ? d : "https://" + d, UriKind.Absolute, out var u))
            d = u.Host;
        return d.StartsWith("www.") ? d[4..] : d;
    }

    public bool Matches(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (Clean(u.Host) == Domain || u.Host.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase));
}

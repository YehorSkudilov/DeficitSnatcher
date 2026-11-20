namespace DeficitSnatcher.Api.Settings;

/// <summary>Everything changed from the web's Settings page. Kept encrypted in the database (see SettingsStore).</summary>
public sealed record BotSettings
{
    /// <summary>The master switch: nothing is bought, on any site, while it's off.</summary>
    public bool AutoBuyArmed { get; init; }

    /// <summary>Checkouts run every step except the final ones (the "Place order" click), then stop.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Most that may be spent in all (every succeeded purchase); 0: no limit beyond each product's.</summary>
    public decimal TotalBudget { get; init; }

    /// <summary>After a failed or dry-run checkout, the same site isn't tried again for this long.</summary>
    public int BuyRetryMinutes { get; init; } = 10;

    public string Currency { get; init; } = "CAD";
    /// <summary>For restock days and times (IANA, e.g. America/Toronto, America/Vancouver).</summary>
    public string TimeZone { get; init; } = "America/Toronto";
    /// <summary>The AI looks up each store's restock pattern online about once a week (one web search per store).</summary>
    public bool AiRestockForecast { get; init; } = true;

    // Checking
    public bool TrackingEnabled { get; init; } = true;
    public int DefaultIntervalSeconds { get; init; } = 60;
    /// <summary>Each wait is randomly up to this much longer or shorter, so checks don't land on a fixed beat.</summary>
    public int JitterPercent { get; init; } = 20;
    /// <summary>A page that keeps failing (3+ in a row) is checked less often, up to 5× its interval. Off: every page keeps
    /// its own interval whatever happens.</summary>
    public bool SlowDownFailingPages { get; init; }
    /// <summary>Two checks of the same host are at least this far apart.</summary>
    public int MinSecondsPerHost { get; init; } = 10;
    /// <summary>Stricter stores: their pages are checked at most every so many minutes, whatever their own interval says.</summary>
    public List<StoreInterval> StoreIntervals { get; init; } = [];
    public int MaxConcurrentChecks { get; init; } = 4;
    public int RequestTimeoutSeconds { get; init; } = 20;
    /// <summary>Stores are reached through this proxy (checks and checkouts; not ChatGPT): http://user:pass@host:port.
    /// A residential proxy gets past stores that block servers' addresses. Empty: direct.</summary>
    public string ProxyUrl { get; init; } = "";
    // The app's browser (BrowserPool).
    /// <summary>The Camoufox Playwright server (DeficitSnatcher.Camoufox), e.g. ws://camoufox:4444/camoufox in Docker.</summary>
    public string CamoufoxUrl { get; init; } = DefaultCamoufoxUrl;
    public const string DefaultCamoufoxUrl = "ws://camoufox:4444/camoufox";
    /// <summary>The server's browser, for Browser-kind pages, checkouts, screenshots and the AI: Camoufox (the default: it
    /// hides automation better) or Chromium (here, in the API's container).</summary>
    public string AppBrowser { get; init; } = "Camoufox";
    /// <summary>A page's browser session (cookies, local storage) is carried from check to check and into checkouts for
    /// this long, then started afresh; 0: every browser starts as a new visitor.</summary>
    public int BrowserSessionMinutes { get; init; } = 180;
    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    /// <summary>Price history and the activity log older than this are removed; 0 keeps it all.</summary>
    public int HistoryDays { get; init; } = 90;
    /// <summary>Scans (every check) older than this are removed; 0 keeps them all.</summary>
    public int ScanHistoryDays { get; init; } = 30;
    /// <summary>Checkouts older than this are removed with their screenshots; 0 keeps them all.</summary>
    public int CheckoutHistoryDays { get; init; } = 0;
    /// <summary>A screenshot after every checkout step (about a tenth of a second each), not only where it ended.</summary>
    public bool StepScreenshots { get; init; } = true;
    /// <summary>Off, Changes (when stock or price changes, it's in range, or the check fails), or Every (each check).
    /// Each is the page's screenshot and source.</summary>
    public string ScanSnapshots { get; init; } = "Changes";

    // AI (ChatGPT): off while OpenAI__ApiKey is unset.
    public bool AiEnabled { get; init; } = true;
    public string AiModel { get; init; } = "gpt-5-mini";
    /// <summary>Sent as reasoning_effort (gpt-5 models): minimal, low, medium, high; empty: not sent (older models).</summary>
    public string AiReasoningEffort { get; init; } = "low";
    /// <summary>A page without checkout steps is bought by the AI going through the checkout itself.</summary>
    public bool AiCheckoutWithoutSteps { get; init; } = true;
    /// <summary>When a checkout step breaks, the AI takes over from that screen and finishes the checkout.</summary>
    public bool AiAutofixCheckout { get; init; } = true;
    /// <summary>The steps the AI took replace the broken ones, so the next checkout runs them without the AI.</summary>
    public bool AiSaveFixedSteps { get; init; } = true;
    /// <summary>When a page keeps failing or showing no price or stock, the AI re-learns how to read it.</summary>
    public bool AiAutofixReading { get; init; } = true;
    /// <summary>Most actions the AI may take in one checkout.</summary>
    public int AiMaxActions { get; init; } = 30;
    /// <summary>Screenshots go to the AI too (never once card details are typed).</summary>
    public bool AiSendScreenshots { get; init; } = true;
    /// <summary>Before the AI places a real order it must read the order total; it may be at most the product's maximum
    /// (times units) plus this much tax and fees.</summary>
    public int MaxTaxPercent { get; init; } = 15;
    /// <summary>Before a saved checkout's final step places a real order, the AI reads the order: only the product, at most
    /// the units per order, total within the limit. Off: saved steps place the order without that check.</summary>
    public bool AiCheckOrder { get; init; } = true;
    /// <summary>Most tokens the AI may use in a day (UTC); once used up the AI rests until the next day and everything else
    /// carries on without it. 0: no limit.</summary>
    public int AiDailyTokenLimit { get; init; } = 300_000;
    /// <summary>After the AI fails on a page (its checkout, or re-learning how to read it), it isn't asked again
    /// automatically for this long. What's started by hand always asks it.</summary>
    public int AiCooldownHours { get; init; } = 6;

    // Notifications
    public string DiscordWebhookUrl { get; init; } = "";
    public string TelegramBotToken { get; init; } = "";
    public string TelegramChatId { get; init; } = "";
    /// <summary>Any URL that takes a plain-text POST (ntfy.sh topics, for one).</summary>
    public string WebhookUrl { get; init; } = "";
    // Email (SMTP), IONOS's settings by default: on once the address to send to, the username and password are set.
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
    /// <summary>In stock in the price range.</summary>
    public bool NotifyInStock { get; init; } = true;
    /// <summary>Came into stock, but outside the range (too expensive, too cheap to be real, or not the right title).</summary>
    public bool NotifyInStockOutOfRange { get; init; } = true;
    /// <summary>A real checkout failed or was cut off.</summary>
    public bool NotifyNotBought { get; init; } = true;
    /// <summary>A dry run finished (off: dry runs while armed would alert every few minutes).</summary>
    public bool NotifyDryRun { get; init; }
    public bool NotifyPriceDrop { get; init; } = true;
    public bool NotifyPurchase { get; init; } = true;
    /// <summary>The old single "problems" switch: what the four below follow until each is set on its own.</summary>
    public bool NotifyErrors { get; init; } = true;
    public bool? NotifyPageFailing { get; init; }
    public bool? NotifyPageRecovered { get; init; }
    public bool? NotifyAiProblems { get; init; }
    public bool? NotifyHelperDisconnected { get; init; }
    /// <summary>A PC helper isn't signed in to a store.</summary>
    public bool NotifySignedOut { get; init; } = true;
    /// <summary>A store wants you to prove you're human on a PC helper.</summary>
    public bool NotifyHumanCheck { get; init; } = true;
    /// <summary>After a helper tries your saved sign-in for a store by itself, it waits this long before trying again (so a
    /// store that flagged it has time to settle, and a wrong password can't lock the account). The button doesn't wait.</summary>
    public int AutoSignInCooldownMinutes { get; init; } = 120;
    /// <summary>A site is reported as failing once this many checks in a row failed (and again when it recovers).</summary>
    public int ErrorThreshold { get; init; } = 5;

    public BotSettings Normalized() => this with
    {
        TotalBudget = Math.Max(0, TotalBudget),
        BuyRetryMinutes = Math.Clamp(BuyRetryMinutes, 0, 24 * 60),
        Currency = string.IsNullOrWhiteSpace(Currency) ? "CAD" : Currency.Trim().ToUpperInvariant(),
        TimeZone = string.IsNullOrWhiteSpace(TimeZone) ? "America/Toronto" : TimeZone.Trim(),
        DefaultIntervalSeconds = Math.Clamp(DefaultIntervalSeconds, 5, 24 * 3600),
        JitterPercent = Math.Clamp(JitterPercent, 0, 90),
        MinSecondsPerHost = Math.Clamp(MinSecondsPerHost, 0, 3600),
        StoreIntervals = (StoreIntervals ?? [])
            .Select(x => new StoreInterval(StoreInterval.Clean(x.Domain), Math.Clamp(x.Minutes, 1, 24 * 60)))
            .Where(x => x.Domain.Length > 0).DistinctBy(x => x.Domain).ToList(),
        MaxConcurrentChecks = Math.Clamp(MaxConcurrentChecks, 1, 32),
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 3, 120),
        UserAgent = string.IsNullOrWhiteSpace(UserAgent) ? new BotSettings().UserAgent : UserAgent.Trim(),
        HistoryDays = Math.Clamp(HistoryDays, 0, 3650),
        ScanHistoryDays = Math.Clamp(ScanHistoryDays, 0, 3650),
        CheckoutHistoryDays = Math.Clamp(CheckoutHistoryDays, 0, 3650),
        ErrorThreshold = Math.Clamp(ErrorThreshold, 1, 1000),
        ScanSnapshots = ScanSnapshots is "Off" or "Every" ? ScanSnapshots : "Changes",
        AiModel = string.IsNullOrWhiteSpace(AiModel) ? "gpt-5-mini" : AiModel.Trim(),
        AiReasoningEffort = (AiReasoningEffort ?? "").Trim().ToLowerInvariant(),
        AiMaxActions = Math.Clamp(AiMaxActions, 5, 100),
        MaxTaxPercent = Math.Clamp(MaxTaxPercent, 0, 50),
        AiDailyTokenLimit = Math.Max(0, AiDailyTokenLimit),
        AiCooldownHours = Math.Clamp(AiCooldownHours, 0, 24 * 7),
        DiscordWebhookUrl = DiscordWebhookUrl?.Trim() ?? "",
        TelegramBotToken = TelegramBotToken?.Trim() ?? "",
        TelegramChatId = TelegramChatId?.Trim() ?? "",
        WebhookUrl = WebhookUrl?.Trim() ?? "",
        EmailTo = EmailTo?.Trim() ?? "",
        SmtpHost = string.IsNullOrWhiteSpace(SmtpHost) ? "smtp.ionos.com" : SmtpHost.Trim(),
        SmtpPort = SmtpPort is > 0 and < 65536 ? SmtpPort : 587,
        SmtpUser = SmtpUser?.Trim() ?? "",
        SmtpPassword = SmtpPassword ?? "",
        EmailFrom = EmailFrom?.Trim() ?? "",
        ProxyUrl = ProxyUrl?.Trim() ?? "",
        CamoufoxUrl = string.IsNullOrWhiteSpace(CamoufoxUrl) ? DefaultCamoufoxUrl : CamoufoxUrl.Trim(),
        AppBrowser = AppBrowser == "Chromium" ? AppBrowser : "Camoufox",
        BrowserSessionMinutes = Math.Clamp(BrowserSessionMinutes, 0, 30 * 24 * 60),
        AutoSignInCooldownMinutes = Math.Clamp(AutoSignInCooldownMinutes, 10, 7 * 24 * 60),
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

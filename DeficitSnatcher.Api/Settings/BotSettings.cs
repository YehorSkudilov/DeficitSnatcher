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

    // Checking
    public bool TrackingEnabled { get; init; } = true;
    public int DefaultIntervalSeconds { get; init; } = 60;
    /// <summary>Each wait is randomly up to this much longer or shorter, so checks don't land on a fixed beat.</summary>
    public int JitterPercent { get; init; } = 20;
    /// <summary>Two checks of the same host are at least this far apart.</summary>
    public int MinSecondsPerHost { get; init; } = 10;
    public int MaxConcurrentChecks { get; init; } = 4;
    public int RequestTimeoutSeconds { get; init; } = 20;
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

    // Notifications
    public string DiscordWebhookUrl { get; init; } = "";
    public string TelegramBotToken { get; init; } = "";
    public string TelegramChatId { get; init; } = "";
    /// <summary>Any URL that takes a plain-text POST (ntfy.sh topics, for one).</summary>
    public string WebhookUrl { get; init; } = "";
    public bool NotifyInStock { get; init; } = true;
    public bool NotifyPriceDrop { get; init; } = true;
    public bool NotifyPurchase { get; init; } = true;
    public bool NotifyErrors { get; init; } = true;
    /// <summary>A site is reported as failing once this many checks in a row failed (and again when it recovers).</summary>
    public int ErrorThreshold { get; init; } = 5;

    public BotSettings Normalized() => this with
    {
        TotalBudget = Math.Max(0, TotalBudget),
        BuyRetryMinutes = Math.Clamp(BuyRetryMinutes, 0, 24 * 60),
        Currency = string.IsNullOrWhiteSpace(Currency) ? "CAD" : Currency.Trim().ToUpperInvariant(),
        DefaultIntervalSeconds = Math.Clamp(DefaultIntervalSeconds, 5, 24 * 3600),
        JitterPercent = Math.Clamp(JitterPercent, 0, 90),
        MinSecondsPerHost = Math.Clamp(MinSecondsPerHost, 0, 3600),
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
        DiscordWebhookUrl = DiscordWebhookUrl?.Trim() ?? "",
        TelegramBotToken = TelegramBotToken?.Trim() ?? "",
        TelegramChatId = TelegramChatId?.Trim() ?? "",
        WebhookUrl = WebhookUrl?.Trim() ?? "",
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

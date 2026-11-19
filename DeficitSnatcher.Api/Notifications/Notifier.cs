using System.Net.Http.Json;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;

namespace DeficitSnatcher.Api.Notifications;

public enum NotifyKind
{
    /// <summary>In stock, in the price range (a match).</summary>
    InStock,
    /// <summary>In stock, but over or under the range (or the title doesn't fit).</summary>
    InStockOutOfRange,
    /// <summary>A lower price while in stock (never for out of stock).</summary>
    PriceDrop,
    /// <summary>An order went through.</summary>
    Purchase,
    /// <summary>A real checkout failed or was cut off.</summary>
    NotBought,
    /// <summary>A dry run finished.</summary>
    DryRun,
    /// <summary>A page keeps failing, the PC helper went away, the AI couldn't fix something.</summary>
    Error,
    /// <summary>The owner has to do something now (a store's human check on the PC): always sent.</summary>
    ActionNeeded,
    Test,
}

/// <summary>
/// Writes the activity log and sends what matters to the owner's phone: Discord (webhook), Telegram (bot) and any
/// plain-text webhook (ntfy.sh, for one), each used when it's set in Settings.
/// </summary>
public sealed class Notifier(IServiceScopeFactory scopes, SettingsStore settingsStore, IHttpClientFactory httpFactory, IConfiguration config,
    ILogger<Notifier> log)
{
    private readonly string _publicUrl = (config["App:PublicUrl"] ?? "").Trim().TrimEnd('/');

    /// <summary>A link to a screen of the site (e.g. "purchase/12"), when App__PublicUrl says where the site is.</summary>
    public string? Link(string route) => _publicUrl.Length == 0 ? null : $"{_publicUrl}/#{route}";

    public async Task LogAsync(EventLevel level, string message, int? siteId = null)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Events.Add(new Event { At = DateTimeOffset.UtcNow, Level = level, SiteId = siteId, Message = message.Length > 4000 ? message[..4000] : message });
            await db.SaveChangesAsync();
        }
        catch (Exception e)
        {
            log.LogError(e, "Couldn't write the activity log: {Message}", message);
        }
    }

    /// <summary>Logs it and sends it out, if that kind of message is turned on.</summary>
    public async Task NotifyAsync(NotifyKind kind, EventLevel level, string message, int? siteId = null, string? url = null)
    {
        await LogAsync(level, message, siteId);
        var s = await settingsStore.GetAsync();
        var wanted = kind switch
        {
            NotifyKind.InStock => s.NotifyInStock,
            NotifyKind.InStockOutOfRange => s.NotifyInStockOutOfRange,
            NotifyKind.PriceDrop => s.NotifyPriceDrop,
            NotifyKind.Purchase => s.NotifyPurchase,
            NotifyKind.NotBought => s.NotifyNotBought,
            NotifyKind.DryRun => s.NotifyDryRun,
            NotifyKind.Error => s.NotifyErrors,
            _ => true,
        };
        if (wanted)
            await SendAsync(s, message, url);
    }

    /// <summary>Sends to every channel that's set; returns what went wrong on each (empty: all fine).</summary>
    public async Task<List<string>> SendAsync(BotSettings s, string message, string? url = null)
    {
        var text = url == null ? message : $"{message}\n{url}";
        var http = httpFactory.CreateClient(nameof(Notifier));
        var problems = new List<string>();

        async Task Try(string channel, Func<Task<HttpResponseMessage>> send)
        {
            try
            {
                using var response = await send();
                if (!response.IsSuccessStatusCode)
                    problems.Add($"{channel}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}".Trim());
            }
            catch (Exception e)
            {
                problems.Add($"{channel}: {e.Message}");
            }
        }

        var tasks = new List<Task>();
        if (s.DiscordWebhookUrl.Length > 0)
            tasks.Add(Try("Discord", () => http.PostAsJsonAsync(s.DiscordWebhookUrl, new { content = Limit(text, 1900), username = "Deficit Snatcher" })));
        if (s.TelegramBotToken.Length > 0 && s.TelegramChatId.Length > 0)
            tasks.Add(Try("Telegram", () => http.PostAsJsonAsync($"https://api.telegram.org/bot{s.TelegramBotToken}/sendMessage",
                new { chat_id = s.TelegramChatId, text = Limit(text, 4000), disable_web_page_preview = true })));
        if (s.WebhookUrl.Length > 0)
            tasks.Add(Try("Webhook", () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, s.WebhookUrl) { Content = new StringContent(text) };
                request.Headers.TryAddWithoutValidation("Title", "Deficit Snatcher");
                if (url != null)
                    request.Headers.TryAddWithoutValidation("Click", url);
                return http.SendAsync(request);
            }));
        if (EmailReady(s))
            tasks.Add(EmailAsync(s, message, url, problems));
        await Task.WhenAll(tasks);
        foreach (var p in problems)
            log.LogWarning("Notification failed: {Problem}", p);
        return problems;
    }

    private static string Limit(string s, int n) => s.Length > n ? s[..n] : s;

    public static bool EmailReady(BotSettings s) => s.EmailTo.Length > 0 && s.SmtpUser.Length > 0 && s.SmtpPassword.Length > 0;

    /// <summary>Emails the alert (SMTP; IONOS by default): the subject is the message's start, the body all of it.</summary>
    private async Task EmailAsync(BotSettings s, string message, string? url, List<string> problems)
    {
        try
        {
            var mail = new MimeKit.MimeMessage();
            mail.From.Add(new MimeKit.MailboxAddress("Deficit Snatcher", s.EmailFrom.Length > 0 ? s.EmailFrom : s.SmtpUser));
            foreach (var to in s.EmailTo.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                mail.To.Add(MimeKit.MailboxAddress.Parse(to));
            var firstLine = message.Split('\n')[0];
            mail.Subject = firstLine.Length > 120 ? firstLine[..117] + "…" : firstLine;
            mail.Body = new MimeKit.TextPart("plain") { Text = url == null ? message : $"{message}\n\n{url}" };

            using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 20000 };
            await smtp.ConnectAsync(s.SmtpHost, s.SmtpPort,
                s.SmtpPort == 465 ? MailKit.Security.SecureSocketOptions.SslOnConnect : MailKit.Security.SecureSocketOptions.StartTls);
            await smtp.AuthenticateAsync(s.SmtpUser, s.SmtpPassword);
            await smtp.SendAsync(new MailKit.SendRequest(mail));
            await smtp.DisconnectAsync(true);
        }
        catch (Exception e)
        {
            lock (problems)
                problems.Add("Email: " + e.Message);
        }
    }
}

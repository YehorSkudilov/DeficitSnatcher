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
    /// <summary>A page failed this many checks in a row.</summary>
    PageFailing,
    /// <summary>A failing page works again.</summary>
    PageRecovered,
    /// <summary>The AI couldn't look at, read or fix a page (or thinks it's the wrong page now).</summary>
    AiProblem,
    Test,
}

/// <summary>
/// Writes the activity log and emails what matters (when email is set up in Settings). Matches also ring the Android app
/// (see Alerts).
/// </summary>
public sealed class Notifier(IServiceScopeFactory scopes, SettingsStore settingsStore, IConfiguration config,
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
    /// <param name="url">The store's product page: where to go to buy it.</param>
    /// <param name="detailsUrl">The page on this site (a checkout's details); default: the store page's own page here.</param>
    public async Task NotifyAsync(NotifyKind kind, EventLevel level, string message, int? siteId = null, string? url = null, string? detailsUrl = null)
    {
        // Only send user-facing alerts for a qualifying in-stock match or a checkout outcome.
        if (kind is not (NotifyKind.InStock or NotifyKind.Purchase or NotifyKind.NotBought or NotifyKind.DryRun))
            return;
        await LogAsync(level, message, siteId);
        var s = await settingsStore.GetAsync();
        var wanted = kind switch
        {
            NotifyKind.InStock => s.NotifyInStock,
            NotifyKind.Purchase => s.NotifyPurchase,
            NotifyKind.NotBought => s.NotifyNotBought,
            NotifyKind.DryRun => s.NotifyDryRun,
            _ => false,
        };
        if (wanted)
            await SendAsync(s, message, url, detailsUrl ?? (siteId is { } id ? Link($"site/{id}") : null));
    }

    /// <summary>Emails it when email is set up; returns what went wrong (empty: all fine).</summary>
    public async Task<List<string>> SendAsync(BotSettings s, string message, string? url = null, string? detailsUrl = null)
    {
        // The store link on its own line (tap to buy), then the details here.
        var text = message + (url == null ? "" : $"\nBuy: {url}") + (detailsUrl == null ? "" : $"\nDetails: {detailsUrl}");
        var problems = new List<string>();
        if (EmailReady(s))
            await EmailAsync(s, message, url, detailsUrl, problems);
        foreach (var p in problems)
            log.LogWarning("Notification failed: {Problem}", p);
        return problems;
    }

    private static string Limit(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>
    /// The email: a big button to the store's product page first (the email is a backup way to buy it), the message, and
    /// a link to the details on this site. Plain text too, for mail apps that don't show HTML.
    /// </summary>
    private static MimeKit.MimeEntity EmailBody(string message, string? url, string? detailsUrl)
    {
        var store = url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : null;
        var builder = new MimeKit.BodyBuilder
        {
            TextBody = (url == null ? "" : $"Buy it here: {url}\n\n") + message + (detailsUrl == null ? "" : $"\n\nDetails: {detailsUrl}"),
        };
        Func<string, string> h = s => System.Net.WebUtility.HtmlEncode(s);
        builder.HtmlBody = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;color:#111">
              {(url == null ? "" : $"""
              <p style="margin:0 0 18px"><a href="{h(url)}" style="display:inline-block;background:#3f7dff;color:#fff;text-decoration:none;font-weight:600;font-size:17px;padding:14px 22px;border-radius:10px">Open {h(store ?? "the store")} to buy</a></p>
              """)}
              <p style="font-size:15px;line-height:1.5;margin:0 0 14px">{h(message).Replace("\n", "<br>")}</p>
              {(url == null ? "" : $"""<p style="font-size:13px;color:#555;margin:0 0 6px;word-break:break-all">Store page: <a href="{h(url)}">{h(url)}</a></p>""")}
              {(detailsUrl == null ? "" : $"""<p style="font-size:13px;color:#555;margin:0">Details: <a href="{h(detailsUrl)}">open in Deficit Snatcher</a></p>""")}
            </div>
            """;
        return builder.ToMessageBody();
    }

    public static bool EmailReady(BotSettings s) => s.EmailTo.Length > 0 && s.SmtpUser.Length > 0 && s.SmtpPassword.Length > 0;

    /// <summary>Emails the alert (SMTP; IONOS by default): the subject is the message's start, the body all of it.</summary>
    private async Task EmailAsync(BotSettings s, string message, string? url, string? detailsUrl, List<string> problems)
    {
        try
        {
            var mail = new MimeKit.MimeMessage();
            mail.From.Add(new MimeKit.MailboxAddress("Deficit Snatcher", s.EmailFrom.Length > 0 ? s.EmailFrom : s.SmtpUser));
            foreach (var to in s.EmailTo.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                mail.To.Add(MimeKit.MailboxAddress.Parse(to));
            var firstLine = message.Split('\n')[0];
            mail.Subject = firstLine.Length > 120 ? firstLine[..117] + "…" : firstLine;
            mail.Body = EmailBody(message, url, detailsUrl);

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

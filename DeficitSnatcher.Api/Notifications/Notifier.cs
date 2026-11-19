using System.Net.Http.Json;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;

namespace DeficitSnatcher.Api.Notifications;

public enum NotifyKind
{
    InStock,
    PriceDrop,
    Purchase,
    Error,
    Test,
}

/// <summary>
/// Writes the activity log and sends what matters to the owner's phone: Discord (webhook), Telegram (bot) and any
/// plain-text webhook (ntfy.sh, for one), each used when it's set in Settings.
/// </summary>
public sealed class Notifier(IServiceScopeFactory scopes, SettingsStore settingsStore, IHttpClientFactory httpFactory, ILogger<Notifier> log)
{
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
            NotifyKind.PriceDrop => s.NotifyPriceDrop,
            NotifyKind.Purchase => s.NotifyPurchase,
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
        await Task.WhenAll(tasks);
        foreach (var p in problems)
            log.LogWarning("Notification failed: {Problem}", p);
        return problems;
    }

    private static string Limit(string s, int n) => s.Length > n ? s[..n] : s;
}

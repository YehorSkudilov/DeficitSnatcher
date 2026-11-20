using System.Collections.Concurrent;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Captchas solved by hand, as StreetMerchant's captcha handler does (src/store/captcha-handler.ts): when a check lands on a
/// store's captcha, the puzzle is screenshotted and shown to the owner (on the site, and by email when it's set up); the
/// check waits for the answer, types it in and goes on. Nothing is solved automatically. Unanswered in time: the check ends
/// as blocked, as before.
/// </summary>
public sealed class Captchas(Notifier notifier, SettingsStore settingsStore, ILogger<Captchas> log)
{
    /// <summary>How long a check waits for the owner's answer.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(2);

    /// <summary>A store's captcha: how to recognise it, where the puzzle is, where the answer goes (StreetMerchant's
    /// captcha labels and captchaHandler).</summary>
    public sealed record Recipe(string Domain, string Container, string[] Text, string Challenge, string Input, string Submit);

    private static readonly Recipe[] Recipes =
    [
        new("amazon.ca", "body", ["enter the characters you see below", "type the characters you see in this image"],
            "form img", "#captchacharacters", "button[type='submit']"),
        new("amazon.com", "body", ["enter the characters you see below", "type the characters you see in this image"],
            "form img", "#captchacharacters", "button[type='submit']"),
    ];

    public static Recipe? For(Uri url) => Recipes.FirstOrDefault(r => url.Host.EndsWith(r.Domain, StringComparison.OrdinalIgnoreCase));

    public sealed record Pending(string Id, int SiteId, string SiteName, string Url, DateTimeOffset AskedAt, DateTimeOffset Until)
    {
        [System.Text.Json.Serialization.JsonIgnore] public byte[] Image { get; init; } = [];
        [System.Text.Json.Serialization.JsonIgnore] public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public IEnumerable<Pending> Open() => _pending.Values.Where(p => p.Until > DateTimeOffset.UtcNow).OrderBy(p => p.AskedAt);

    public Pending? Get(string id) => _pending.TryGetValue(id, out var p) ? p : null;

    public bool Answer(string id, string answer) => _pending.TryGetValue(id, out var p) && p.Answer.TrySetResult(answer.Trim());

    /// <summary>The page shows this store's captcha: asks the owner, types their answer in and sends it. True when an answer
    /// was submitted (the page should be read again).</summary>
    public async Task<bool> SolveByHandAsync(IPage page, Recipe recipe, int siteId, string siteName, string url, CancellationToken ct)
    {
        IElementHandle? challenge;
        try
        {
            challenge = await page.QuerySelectorAsync(recipe.Challenge);
        }
        catch (PlaywrightException)
        {
            challenge = null;
        }
        if (challenge == null)
        {
            log.LogInformation("Captcha on {Site}, but its puzzle wasn't found ({Selector})", siteName, recipe.Challenge);
            return false;
        }
        var image = await challenge.ScreenshotAsync();
        var now = DateTimeOffset.UtcNow;
        var pending = new Pending(Guid.NewGuid().ToString("N")[..12], siteId, siteName, url, now, now + Wait) { Image = image };
        _pending[pending.Id] = pending;
        try
        {
            var message = $"{siteName} is showing a captcha. Type what it says on the site within {Wait.TotalMinutes:0} minutes "
                + $"(until {Times.At(pending.Until, "time")}) and the check goes on.";
            await notifier.LogAsync(Data.EventLevel.Warning, message, siteId);
            var settings = await settingsStore.GetAsync(ct);
            if (Notifier.EmailReady(settings))
                _ = notifier.SendAsync(settings, message, null, notifier.Link("dashboard"));
            log.LogInformation("Captcha on {Site}: waiting up to {Minutes} min for the owner's answer", siteName, Wait.TotalMinutes);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Wait);
            string? answer;
            try
            {
                answer = await pending.Answer.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.LogInformation("Captcha on {Site}: no answer in time", siteName);
                return false;
            }
            if (string.IsNullOrEmpty(answer))
                return false;
            await page.FillAsync(recipe.Input, answer);
            await page.WaitForTimeoutAsync(400 + Random.Shared.Next(600));
            await page.ClickAsync(recipe.Submit);
            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 20000 });
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8000 });
            }
            catch (TimeoutException)
            {
            }
            log.LogInformation("Captcha on {Site}: answer sent", siteName);
            return true;
        }
        catch (PlaywrightException e)
        {
            log.LogInformation("Captcha on {Site}: couldn't send the answer ({Error})", siteName, e.Message.Split('\n')[0]);
            return false;
        }
        finally
        {
            _pending.TryRemove(pending.Id, out _);
        }
    }

    public static bool Showing(string html, Recipe recipe) =>
        recipe.Text.Any(t => html.Contains(t, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Captchas waiting for the owner (see <see cref="Captchas"/>).</summary>
[ApiController]
[Route("api/captchas")]
public sealed class CaptchasController(Captchas captchas) : ControllerBase
{
    [HttpGet]
    public IEnumerable<Captchas.Pending> Open() => captchas.Open();

    [HttpGet("{id}/image")]
    public IActionResult Image(string id) => captchas.Get(id) is { } p ? File(p.Image, "image/png") : NotFound();

    public sealed record AnswerRequest(string Answer);

    [HttpPost("{id}")]
    public IActionResult Answer(string id, AnswerRequest request) =>
        string.IsNullOrWhiteSpace(request.Answer) ? Problem("Type the characters first.", statusCode: 400)
        : captchas.Answer(id, request.Answer) ? NoContent()
        : Problem("That captcha has expired; the next check will ask again.", statusCode: 404);
}

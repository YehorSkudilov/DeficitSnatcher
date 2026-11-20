using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Settings;
using Microsoft.AspNetCore.Mvc;

namespace DeficitSnatcher.Api.Controllers;

public sealed record ArmRequest(bool Armed);
public sealed record NotificationTestResponse(bool Sent, List<string> Problems);

/// <summary>The checkout profile as the web sees it: the card number and CVV are never sent back.</summary>
public sealed record ProfileDto(
    string Email,
    string Phone,
    string FirstName,
    string LastName,
    string Address1,
    string Address2,
    string City,
    string Province,
    string PostalCode,
    string Country,
    string CardName,
    /// <summary>Read: the last 4 digits ("" when there's none). Written: the new number, or "" to keep the saved one.</summary>
    string CardNumber,
    string CardExpiryMonth,
    string CardExpiryYear,
    /// <summary>Read: always "". Written: the new CVV, or "" to keep the saved one.</summary>
    string CardCvv,
    bool HasCvv,
    bool ClearCard);

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(SettingsStore store, Notifier notifier) : ControllerBase
{
    [HttpGet]
    public Task<BotSettings> Get(CancellationToken ct) => store.GetAsync(ct);

    [HttpPut]
    public async Task<ActionResult<BotSettings>> Save(BotSettings settings, CancellationToken ct)
    {
        if (Tracking.StoreProxy.Validate(settings.ProxyUrl) is { } proxyError)
            return Problem(proxyError, statusCode: 400);
        var before = await store.GetAsync(ct);
        var saved = await store.SaveAsync(settings, ct);
        await LogArmChange(before, saved);
        return saved;
    }

    /// <summary>The dashboard's big switch.</summary>
    [HttpPost("arm")]
    public async Task<BotSettings> Arm(ArmRequest request, CancellationToken ct)
    {
        var before = await store.GetAsync(ct);
        var saved = await store.UpdateAsync(s => s with { AutoBuyArmed = request.Armed }, ct);
        await LogArmChange(before, saved);
        return saved;
    }

    [HttpPost("test-notification")]
    public async Task<NotificationTestResponse> TestNotification(CancellationToken ct)
    {
        var s = await store.GetAsync(ct);
        if (!Notifier.EmailReady(s))
            return new(false, ["Email isn't set up: the address to send to, the SMTP username and password."]);
        var problems = await notifier.SendAsync(s, "Deficit Snatcher test: notifications work.");
        return new(problems.Count == 0, problems);
    }

    [HttpGet("profile")]
    public async Task<ProfileDto> GetProfile(CancellationToken ct) => ToDto(await store.GetProfileAsync(ct));

    [HttpPut("profile")]
    public async Task<ProfileDto> SaveProfile(ProfileDto input, CancellationToken ct)
    {
        var saved = await store.GetProfileAsync(ct);
        var digits = new string((input.CardNumber ?? "").Where(char.IsDigit).ToArray());
        var profile = new CheckoutProfile
        {
            Email = T(input.Email),
            Phone = T(input.Phone),
            FirstName = T(input.FirstName),
            LastName = T(input.LastName),
            Address1 = T(input.Address1),
            Address2 = T(input.Address2),
            City = T(input.City),
            Province = T(input.Province),
            PostalCode = T(input.PostalCode),
            Country = T(input.Country),
            CardName = T(input.CardName),
            // Fewer than 12 digits is the "•••• 1234" shown back, not a new number.
            CardNumber = input.ClearCard ? "" : digits.Length >= 12 ? digits : saved.CardNumber,
            CardExpiryMonth = T(input.CardExpiryMonth),
            CardExpiryYear = T(input.CardExpiryYear),
            CardCvv = input.ClearCard ? "" : T(input.CardCvv) is { Length: > 0 } cvv ? cvv : saved.CardCvv,
        };
        await store.SaveProfileAsync(profile, ct);
        return ToDto(profile);
    }

    private static string T(string? s) => s?.Trim() ?? "";

    private static ProfileDto ToDto(CheckoutProfile p) => new(p.Email, p.Phone, p.FirstName, p.LastName, p.Address1, p.Address2,
        p.City, p.Province, p.PostalCode, p.Country, p.CardName, p.CardNumber.Length >= 4 ? p.CardNumber[^4..] : "",
        p.CardExpiryMonth, p.CardExpiryYear, "", p.CardCvv.Length > 0, false);

    private async Task LogArmChange(BotSettings before, BotSettings after)
    {
        if (before.AutoBuyArmed != after.AutoBuyArmed || after.AutoBuyArmed && before.DryRun != after.DryRun)
            await notifier.LogAsync(after.AutoBuyArmed ? EventLevel.Warning : EventLevel.Info,
                after.AutoBuyArmed
                    ? "Auto-buy on: matches will be bought."
                    : "Auto-buy disarmed.");
    }
}

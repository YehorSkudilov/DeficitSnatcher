using DeficitSnatcher.Api.Settings;
using Microsoft.AspNetCore.Mvc;

namespace DeficitSnatcher.Api.Controllers;

public sealed record StoreAccountDto(string Domain, string Email, bool HasPassword, string? SignInUrl, string? LearnedSignInUrl);
public sealed record StoreAccountInput(string Email, string? Password, string? SignInUrl = null);

/// <summary>Your store sign-ins. The password is never sent back: only whether there is one.</summary>
[ApiController]
[Route("api/store-accounts")]
public sealed class StoreAccountsController(SettingsStore store, Pc.LoginWatcher logins) : ControllerBase
{
    [HttpGet]
    public async Task<List<StoreAccountDto>> List(CancellationToken ct) =>
        (await store.GetStoreAccountsAsync(ct)).OrderBy(a => a.Domain).Select(a => new StoreAccountDto(a.Domain, a.Email, a.Password.Length > 0, a.SignInUrl, a.LearnedSignInUrl)).ToList();

    /// <summary>Sets the email, and the password when one is given (empty or missing: keeps the saved one).</summary>
    [HttpPut("{domain}")]
    public async Task<IActionResult> Save(string domain, StoreAccountInput input, CancellationToken ct)
    {
        domain = domain.Trim().ToLowerInvariant();
        if (domain.StartsWith("www."))
            domain = domain[4..];
        if (string.IsNullOrWhiteSpace(input.Email))
            return BadRequest(new { error = "The email (or username) is needed." });
        var all = await store.GetStoreAccountsAsync(ct);
        var old = all.FirstOrDefault(a => a.Domain == domain);
        var password = string.IsNullOrEmpty(input.Password) ? old?.Password ?? "" : input.Password;
        if (password.Length == 0)
            return BadRequest(new { error = "The password is needed." });
        var signInUrl = string.IsNullOrWhiteSpace(input.SignInUrl) ? null : input.SignInUrl.Trim();
        if (signInUrl != null && !(Uri.TryCreate(signInUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            return BadRequest(new { error = "The sign-in page must be a web address (https://…)." });
        all.RemoveAll(a => a.Domain == domain);
        all.Add(new StoreAccount(domain, input.Email.Trim(), password, signInUrl, old?.LearnedSignInUrl));
        await store.SaveStoreAccountsAsync(all, ct);
        // A saved sign-in decides whether a signed-out helper sits the store out.
        await logins.RefreshPausedAsync();
        return NoContent();
    }

    [HttpDelete("{domain}")]
    public async Task<IActionResult> Delete(string domain, CancellationToken ct)
    {
        var all = await store.GetStoreAccountsAsync(ct);
        all.RemoveAll(a => a.Domain == domain.Trim().ToLowerInvariant());
        await store.SaveStoreAccountsAsync(all, ct);
        // A saved sign-in decides whether a signed-out helper sits the store out.
        await logins.RefreshPausedAsync();
        return NoContent();
    }
}

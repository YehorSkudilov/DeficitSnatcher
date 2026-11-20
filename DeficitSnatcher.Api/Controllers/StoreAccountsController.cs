using DeficitSnatcher.Api.Settings;
using Microsoft.AspNetCore.Mvc;

namespace DeficitSnatcher.Api.Controllers;

public sealed record StoreAccountDto(string Domain, string Email, bool HasPassword);
public sealed record StoreAccountInput(string Email, string? Password);

/// <summary>Your store sign-ins. The password is never sent back: only whether there is one.</summary>
[ApiController]
[Route("api/store-accounts")]
public sealed class StoreAccountsController(SettingsStore store) : ControllerBase
{
    [HttpGet]
    public async Task<List<StoreAccountDto>> List(CancellationToken ct) =>
        (await store.GetStoreAccountsAsync(ct)).OrderBy(a => a.Domain).Select(a => new StoreAccountDto(a.Domain, a.Email, a.Password.Length > 0)).ToList();

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
        all.RemoveAll(a => a.Domain == domain);
        all.Add(new StoreAccount(domain, input.Email.Trim(), password));
        await store.SaveStoreAccountsAsync(all, ct);
        return NoContent();
    }

    [HttpDelete("{domain}")]
    public async Task<IActionResult> Delete(string domain, CancellationToken ct)
    {
        var all = await store.GetStoreAccountsAsync(ct);
        all.RemoveAll(a => a.Domain == domain.Trim().ToLowerInvariant());
        await store.SaveStoreAccountsAsync(all, ct);
        return NoContent();
    }
}

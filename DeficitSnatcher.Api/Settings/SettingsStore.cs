using System.Text.Json;
using DeficitSnatcher.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Settings;

/// <summary>The settings and the checkout profile: kept encrypted in the database, cached here once read.</summary>
public sealed class SettingsStore(IServiceScopeFactory scopes, SecretBox box)
{
    private const string SettingsKey = "settings";
    private const string ProfileKey = "checkout-profile";
    private const string StoreLoginsKey = "store-logins";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private BotSettings? _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Alerts only is on (from the settings in memory; false until they're first read).</summary>
    public bool AlertsOnly => _settings?.AlertsOnly == true;

    public async Task<BotSettings> GetAsync(CancellationToken ct = default) =>
        _settings ??= (await ReadAsync<BotSettings>(SettingsKey, ct) ?? new BotSettings()).Normalized();

    public async Task<BotSettings> SaveAsync(BotSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var normalized = settings.Normalized();
            await WriteAsync(SettingsKey, normalized, ct);
            _settings = normalized;
            return normalized;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BotSettings> UpdateAsync(Func<BotSettings, BotSettings> change, CancellationToken ct = default) =>
        await SaveAsync(change(await GetAsync(ct)), ct);

    public async Task<CheckoutProfile> GetProfileAsync(CancellationToken ct = default) =>
        await ReadAsync<CheckoutProfile>(ProfileKey, ct) ?? new CheckoutProfile();

    public Task SaveProfileAsync(CheckoutProfile profile, CancellationToken ct = default) => WriteAsync(ProfileKey, profile, ct);

    /// <summary>Your sign-ins for the stores (encrypted, like the card): what helpers sign in with when they're signed out.</summary>
    public async Task<List<StoreAccount>> GetStoreAccountsAsync(CancellationToken ct = default) =>
        await ReadAsync<List<StoreAccount>>(StoreLoginsKey, ct) ?? [];

    public Task SaveStoreAccountsAsync(List<StoreAccount> accounts, CancellationToken ct = default) => WriteAsync(StoreLoginsKey, accounts, ct);

    private async Task<T?> ReadAsync<T>(string key, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StoredValues.AsNoTracking().FirstOrDefaultAsync(v => v.Key == key, ct);
        return row == null ? default : JsonSerializer.Deserialize<T>(box.Decrypt(row.Value), Json);
    }

    private async Task WriteAsync<T>(string key, T value, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sealedValue = box.Encrypt(JsonSerializer.Serialize(value, Json));
        var row = await db.StoredValues.FirstOrDefaultAsync(v => v.Key == key, ct);
        if (row == null)
            db.StoredValues.Add(new StoredValue { Key = key, Value = sealedValue });
        else
            row.Value = sealedValue;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Your account at a store (domain, e.g. walmart.ca). <see cref="SignInUrl"/>: its sign-in page if you gave it;
/// <see cref="LearnedSignInUrl"/>: the one a helper last signed in on (found by itself, or by the AI).</summary>
public sealed record StoreAccount(string Domain, string Email, string Password, string? SignInUrl = null, string? LearnedSignInUrl = null);

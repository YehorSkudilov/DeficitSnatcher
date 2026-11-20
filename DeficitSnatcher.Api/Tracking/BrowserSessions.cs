using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Each store page's browser session on the server (see <see cref="BrowserSession"/>): loaded into the browser before a
/// check, checkout or AI setup of that page, and saved again when it's done. A session is started afresh when it's older
/// than Settings → Keep browser sessions, when it came from another browser, when the store blocked it, or when the owner
/// resets it on the page.
/// </summary>
public sealed class BrowserSessions(IServiceScopeFactory scopes, SecretBox box, ILogger<BrowserSessions> log)
{
    /// <summary>The saved storage state for the page in this browser, or null to start afresh.</summary>
    public async Task<string?> LoadAsync(int siteId, string engine, BotSettings settings, CancellationToken ct)
    {
        if (siteId <= 0 || settings.BrowserSessionMinutes <= 0)
            return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.BrowserSessions.AsNoTracking().FirstOrDefaultAsync(x => x.SiteId == siteId, ct);
        if (saved == null)
            return null;
        string? why = null;
        if (saved.Engine != engine)
            why = $"it came from {saved.Engine}, this is {engine}";
        else if (DateTimeOffset.UtcNow - saved.StartedAt > TimeSpan.FromMinutes(settings.BrowserSessionMinutes))
            why = "it expired";
        if (why == null)
            try
            {
                return box.Decrypt(saved.StateEncrypted);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                why = "it can't be decrypted (the secrets key changed)";
            }
        log.LogInformation("Site {Site}: starting a new browser session ({Why})", siteId, why);
        await db.BrowserSessions.Where(x => x.SiteId == siteId).ExecuteDeleteAsync(ct);
        return null;
    }

    public async Task SaveAsync(int siteId, string engine, string state)
    {
        if (siteId <= 0 || string.IsNullOrEmpty(state))
            return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var row = await db.BrowserSessions.FirstOrDefaultAsync(x => x.SiteId == siteId);
            if (row == null || row.Engine != engine)
            {
                if (row != null)
                    db.BrowserSessions.Remove(row);
                if (!await db.Sites.AnyAsync(s => s.Id == siteId))
                    return;
                row = new BrowserSession { SiteId = siteId, Engine = engine, StartedAt = now };
                db.BrowserSessions.Add(row);
            }
            row.StateEncrypted = box.Encrypt(state);
            row.SavedAt = now;
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e)
        {
            // Two jobs on the same page finishing at once: the other one's session is kept, which is just as good.
            log.LogDebug("Site {Site}: browser session not saved: {Error}", siteId, e.Message);
        }
    }

    /// <summary>Forgets the page's session: the next browser comes as someone new.</summary>
    public async Task ForgetAsync(int siteId, string why)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.BrowserSessions.Where(x => x.SiteId == siteId).ExecuteDeleteAsync() > 0)
            log.LogInformation("Site {Site}: browser session dropped ({Why})", siteId, why);
    }

    /// <summary>When the page's session started and was last saved, and in which browser; null: none.</summary>
    public async Task<(string Engine, DateTimeOffset StartedAt, DateTimeOffset SavedAt)?> InfoAsync(int siteId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.BrowserSessions.AsNoTracking().Where(x => x.SiteId == siteId)
            .Select(x => new { x.Engine, x.StartedAt, x.SavedAt }).FirstOrDefaultAsync(ct);
        return row == null ? null : (row.Engine, row.StartedAt, row.SavedAt);
    }
}

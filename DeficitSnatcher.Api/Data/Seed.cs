using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Data;

/// <summary>
/// A new database starts with the PS5 Pro to buy (no pages yet): only the Pro, never the regular, Slim or Digital PS5.
/// Edit its range on the web.
/// </summary>
public static class Seed
{
    /// <summary>The title must say PS5 Pro (keywords compare without spaces, case or ®/™: see BuyRules.Normalize).</summary>
    public const string Ps5ProRequired = "PS5 Pro|PlayStation 5 Pro";

    /// <summary>Other consoles and things sold "for PS5 Pro" that aren't the console.</summary>
    public const string Ps5ProExcluded =
        "Slim, Digital Edition, Disc Edition, PS4, Portal, DualSense, controller, carrying case, console cover, faceplate, skin, "
        + "vertical stand, charging, refurbished, renewed, pre-owned, open box";

    // Canadian prices: a real PS5 Pro is over $800 (a Slim is $550–700); up to $1,050 is around its list price.
    public const decimal Ps5ProMin = 800;
    public const decimal Ps5ProMax = 1050;

    // What the first version seeded, so a database made with it is moved to the PS5 Pro-only defaults.
    private const string OldRequired = "Pro, PS5|PlayStation 5|PlayStation®5|PlayStation® 5";

    public static async Task RunAsync(AppDbContext db)
    {
        // A database seeded by the first version: its keywords also let a Slim with a "Pro…" word in the title through.
        var old = await db.Products.FirstOrDefaultAsync(p => p.Name == "PS5 Pro" && p.RequiredKeywords == OldRequired);
        if (old != null)
        {
            old.RequiredKeywords = Ps5ProRequired;
            old.ExcludedKeywords = Ps5ProExcluded;
            if (old.MinPrice == 600 && old.MaxPrice == 1000)
                (old.MinPrice, old.MaxPrice) = (Ps5ProMin, Ps5ProMax);
            await db.SaveChangesAsync();
        }

        if (await db.Products.AnyAsync())
            return;
        db.Products.Add(new Product
        {
            Name = "PS5 Pro",
            Enabled = true,
            MinPrice = Ps5ProMin,
            MaxPrice = Ps5ProMax,
            MaxQuantity = 1,
            QuantityPerOrder = 1,
            RequiredKeywords = Ps5ProRequired,
            ExcludedKeywords = Ps5ProExcluded,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}

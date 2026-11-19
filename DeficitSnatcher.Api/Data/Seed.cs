using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Data;

/// <summary>A new database starts with the PS5 Pro to buy (no sites yet): edit its range on the web.</summary>
public static class Seed
{
    public static async Task RunAsync(AppDbContext db)
    {
        if (await db.Products.AnyAsync())
            return;
        db.Products.Add(new Product
        {
            Name = "PS5 Pro",
            Enabled = true,
            MinPrice = 600,
            MaxPrice = 1000,
            MaxQuantity = 1,
            QuantityPerOrder = 1,
            RequiredKeywords = "Pro, PS5|PlayStation 5|PlayStation®5|PlayStation® 5",
            ExcludedKeywords = "controller, DualSense, case, cover, skin, stand, charging, refurbished, pre-owned, faceplate",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}

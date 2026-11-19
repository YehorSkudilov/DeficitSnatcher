using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<PricePoint> PricePoints => Set<PricePoint>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<StoredValue> StoredValues => Set<StoredValue>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.MinPrice).HasPrecision(12, 2);
            e.Property(p => p.MaxPrice).HasPrecision(12, 2);
            e.Property(p => p.RequiredKeywords).HasMaxLength(1000);
            e.Property(p => p.ExcludedKeywords).HasMaxLength(1000);
        });
        b.Entity<Site>(e =>
        {
            e.HasOne(s => s.Product).WithMany(p => p.Sites).HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.Property(s => s.Name).HasMaxLength(200);
            e.Property(s => s.Url).HasMaxLength(2000);
            e.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.ShippingCost).HasPrecision(12, 2);
            e.Property(s => s.LastPrice).HasPrecision(12, 2);
        });
        b.Entity<PricePoint>(e =>
        {
            e.HasIndex(p => new { p.SiteId, p.At });
            e.Property(p => p.Price).HasPrecision(12, 2);
            e.HasOne<Site>().WithMany().HasForeignKey(p => p.SiteId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Purchase>(e =>
        {
            e.HasIndex(p => p.StartedAt);
            e.Property(p => p.Price).HasPrecision(12, 2);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Site>().WithMany().HasForeignKey(p => p.SiteId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Event>(e =>
        {
            e.HasIndex(x => x.At);
            e.Property(x => x.Level).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Message).HasMaxLength(4000);
        });
        b.Entity<StoredValue>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });
    }
}

using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<PricePoint> PricePoints => Set<PricePoint>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Scan> Scans => Set<Scan>();
    public DbSet<PurchaseShot> PurchaseShots => Set<PurchaseShot>();
    public DbSet<ScanSnapshot> ScanSnapshots => Set<ScanSnapshot>();
    public DbSet<AiCall> AiCalls => Set<AiCall>();
    public DbSet<RestockInsight> RestockInsights => Set<RestockInsight>();
    public DbSet<StoredValue> StoredValues => Set<StoredValue>();
    public DbSet<BrowserSession> BrowserSessions => Set<BrowserSession>();

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
            e.Property(p => p.Worker).HasMaxLength(40);
            e.HasOne<Site>().WithMany().HasForeignKey(p => p.SiteId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PurchaseShot>(e =>
        {
            e.HasIndex(x => new { x.PurchaseId, x.Id });
            e.Property(x => x.Label).HasMaxLength(500);
            e.Property(x => x.ContentType).HasMaxLength(50);
            e.HasOne<Purchase>().WithMany().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Scan>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.SiteId, x.At });
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Price).HasPrecision(12, 2);
            e.Property(x => x.TotalPrice).HasPrecision(12, 2);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Verdict).HasMaxLength(500);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.Action).HasMaxLength(1000);
            e.Property(x => x.Worker).HasMaxLength(40);
            e.HasIndex(x => new { x.Worker, x.At });
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ScanSnapshot>(e =>
        {
            e.HasKey(x => x.ScanId);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.SourceType).HasMaxLength(50);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasOne<Scan>().WithOne().HasForeignKey<ScanSnapshot>(x => x.ScanId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<BrowserSession>(e =>
        {
            e.HasKey(x => x.SiteId);
            e.Property(x => x.Engine).HasMaxLength(20);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RestockInsight>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.Domain }).IsUnique();
            e.Property(x => x.Domain).HasMaxLength(200);
            e.Property(x => x.Pattern).HasMaxLength(500);
            e.Property(x => x.Confidence).HasMaxLength(20);
            e.Property(x => x.Summary).HasMaxLength(4000);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AiCall>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.SiteId, x.At });
            e.Property(x => x.Purpose).HasMaxLength(50);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Summary).HasMaxLength(8000);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
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

using System.Text;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PcServ.Core.Entities;

namespace PcServ.Data;

public class PcServDbContext(DbContextOptions<PcServDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    /// <summary>
    /// The user name recorded in CreatedBy/UpdatedBy and the audit log.
    /// Set by AppDbFactory in the web app; falls back to "system" (seeding, startup tasks).
    /// </summary>
    public string CurrentUserName { get; set; } = "system";

    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<LinkCategory> LinkCategories => Set<LinkCategory>();
    public DbSet<LinkItem> Links => Set<LinkItem>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<SiteMap> SiteMaps => Set<SiteMap>();
    public DbSet<MapLayer> MapLayers => Set<MapLayer>();
    public DbSet<MapFeature> MapFeatures => Set<MapFeature>();
    public DbSet<MapLink> MapLinks => Set<MapLink>();
    public DbSet<MapFeaturePlacement> MapFeaturePlacements => Set<MapFeaturePlacement>();
    public DbSet<MapPhoto> MapPhotos => Set<MapPhoto>();
    public DbSet<MapLegendEntry> MapLegendEntries => Set<MapLegendEntry>();
    public DbSet<SiteStaffMember> SiteStaffMembers => Set<SiteStaffMember>();
    public DbSet<WikiSection> WikiSections => Set<WikiSection>();
    public DbSet<WikiPage> WikiPages => Set<WikiPage>();
    public DbSet<WikiPageVersion> WikiPageVersions => Set<WikiPageVersion>();
    public DbSet<WikiDraft> WikiDrafts => Set<WikiDraft>();
    public DbSet<WikiImage> WikiImages => Set<WikiImage>();
    public DbSet<RepairListItem> RepairListItems => Set<RepairListItem>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Repair> Repairs => Set<Repair>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderStatusChange> OrderStatusChanges => Set<OrderStatusChange>();

    /// <summary>
    /// Skips the per-record audit entries for this save. For bulk imports, which would otherwise
    /// write thousands of lines. The caller must add one summary entry with <see cref="LogAudit"/>.
    /// </summary>
    public bool SuppressEntityAudit { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Site>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Type).HasConversion<string>().HasMaxLength(20);
            e.HasMany(s => s.Buildings).WithOne(b => b.Site!).HasForeignKey(b => b.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LinkCategory>(e =>
        {
            e.HasIndex(c => c.Name).IsUnique();
            e.HasMany(c => c.Links).WithOne(l => l.Category!).HasForeignKey(l => l.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SiteMap>(e =>
        {
            e.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(m => m.StorageKey).IsUnique();
            // A site's maps must be deleted first, so their files get removed from disk too.
            e.HasOne(m => m.Site).WithMany().HasForeignKey(m => m.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.Building).WithMany().HasForeignKey(m => m.BuildingId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(m => m.Layers).WithOne(l => l.SiteMap!).HasForeignKey(l => l.SiteMapId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MapLayer>(e =>
        {
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasMany(l => l.Features).WithOne(f => f.MapLayer!).HasForeignKey(f => f.MapLayerId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MapFeature>(e =>
        {
            e.Property(f => f.Type).HasConversion<string>().HasMaxLength(20);
            e.HasOne(f => f.LegendEntry).WithMany().HasForeignKey(f => f.LegendEntryId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Order>(e =>
        {
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(o => o.Status);

            // Orders are records: a site or vendor can't quietly take them along when it's deleted.
            e.HasOne(o => o.Site).WithMany().HasForeignKey(o => o.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.Vendor).WithMany().HasForeignKey(o => o.VendorId).OnDelete(DeleteBehavior.Restrict);

            // Losing a pin, or the map it was on, doesn't lose the order.
            e.HasOne(o => o.MapFeature).WithOne(f => f.Order).HasForeignKey<Order>(o => o.MapFeatureId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<WikiPage>(e => e.HasOne(p => p.Section).WithMany(s => s.Pages).HasForeignKey(p => p.SectionId).OnDelete(DeleteBehavior.Restrict));
        builder.Entity<WikiPageVersion>(e =>
        {
            e.HasIndex(v => new { v.PageId, v.Number }).IsUnique();
            e.HasOne(v => v.Page).WithMany(p => p.Versions).HasForeignKey(v => v.PageId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<WikiDraft>(e =>
        {
            e.HasIndex(d => new { d.PageId, d.UserName }).IsUnique();
            e.HasOne(d => d.Page).WithMany().HasForeignKey(d => d.PageId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<WikiImage>(e => e.HasIndex(i => i.StorageKey).IsUnique());

        builder.Entity<RepairListItem>(e => e.Property(i => i.Kind).HasConversion<string>().HasMaxLength(20));

        builder.Entity<Device>(e =>
        {
            // Asset tags are matched whatever case they're scanned or typed in.
            e.Property(d => d.AssetTag).UseCollation("NOCASE");
            e.HasIndex(d => d.AssetTag).IsUnique();
            e.Property(d => d.SerialNumber).UseCollation("NOCASE");
            e.HasIndex(d => d.SerialNumber);
            e.HasOne(d => d.Model).WithMany().HasForeignKey(d => d.ModelId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Site).WithMany().HasForeignKey(d => d.SiteId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Repair>(e =>
        {
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.InventoryUpdatedTo).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(r => r.Status);
            e.HasOne(r => r.Device).WithMany(d => d.Repairs).HasForeignKey(r => r.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Site).WithMany().HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Reason).WithMany().HasForeignKey(r => r.ReasonId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SiteStaffMember>(e =>
        {
            e.HasIndex(s => s.SiteId);
            e.HasOne(s => s.Site).WithMany().HasForeignKey(s => s.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OrderStatusChange>(e =>
        {
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(c => c.Order).WithMany(o => o.History).HasForeignKey(c => c.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MapLegendEntry>(e =>
            e.HasOne(l => l.MapLayer).WithMany(l => l.LegendEntries).HasForeignKey(l => l.MapLayerId).OnDelete(DeleteBehavior.Cascade));

        builder.Entity<MapLink>(e =>
        {
            e.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(l => new { l.SourceMapId, l.TargetMapId }).IsUnique();
            e.HasOne(l => l.SourceMap).WithMany().HasForeignKey(l => l.SourceMapId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.TargetMap).WithMany().HasForeignKey(l => l.TargetMapId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MapPhoto>(e =>
        {
            e.HasIndex(p => p.StorageKey).IsUnique();
            e.HasOne(p => p.MapFeature).WithMany(f => f.Photos).HasForeignKey(p => p.MapFeatureId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MapFeaturePlacement>(e =>
        {
            e.HasIndex(p => new { p.MapLinkId, p.MapFeatureId }).IsUnique();
            e.HasOne(p => p.MapLink).WithMany().HasForeignKey(p => p.MapLinkId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.MapFeature).WithMany().HasForeignKey(p => p.MapFeatureId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditEntry>(e =>
        {
            e.HasIndex(a => a.Timestamp);
            e.HasIndex(a => new { a.EntityType, a.EntityId });
        });
    }

    /// <summary>Adds a custom audit entry (e.g. for user management actions that aren't entity edits).</summary>
    public void LogAudit(string action, string entityType, string? entityId, string summary)
    {
        AuditEntries.Add(new AuditEntry
        {
            Timestamp = DateTime.UtcNow,
            UserName = CurrentUserName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Summary = Truncate(summary, 4000),
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var pending = StampAndCollectAudit();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (WriteAddedAudit(pending)) base.SaveChanges(acceptAllChangesOnSuccess);
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var pending = StampAndCollectAudit();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (WriteAddedAudit(pending)) await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        return result;
    }

    /// <summary>
    /// Fills in Created/Updated stamps and writes audit entries for updates and deletes.
    /// New records don't have an Id until saved, so their audit entries are written right after.
    /// </summary>
    private List<EntityEntry<Entity>> StampAndCollectAudit()
    {
        var now = DateTime.UtcNow;
        var added = new List<EntityEntry<Entity>>();

        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = CurrentUserName;
                    if (!SuppressEntityAudit) added.Add(entry);
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = CurrentUserName;
                    var changes = DescribeChanges(entry);
                    if (changes.Length > 0 && !SuppressEntityAudit)
                    {
                        LogAudit("Updated", entry.Metadata.ClrType.Name, entry.Entity.Id.ToString(), $"{DisplayNameOf(entry.Entity)}: {changes}");
                    }
                    break;

                case EntityState.Deleted:
                    if (!SuppressEntityAudit)
                    {
                        LogAudit("Deleted", entry.Metadata.ClrType.Name, entry.Entity.Id.ToString(), DisplayNameOf(entry.Entity));
                    }
                    break;
            }
        }

        return added;
    }

    private bool WriteAddedAudit(List<EntityEntry<Entity>> added)
    {
        foreach (var entry in added)
        {
            LogAudit("Created", entry.Metadata.ClrType.Name, entry.Entity.Id.ToString(), DisplayNameOf(entry.Entity));
        }
        return added.Count > 0;
    }

    private static readonly HashSet<string> IgnoredProperties =
        [nameof(Entity.CreatedAt), nameof(Entity.CreatedBy), nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy)];

    private static string DescribeChanges(EntityEntry entry)
    {
        var sb = new StringBuilder();
        foreach (var prop in entry.Properties)
        {
            if (!prop.IsModified || IgnoredProperties.Contains(prop.Metadata.Name)) continue;
            if (Equals(prop.OriginalValue, prop.CurrentValue)) continue;

            if (sb.Length > 0) sb.Append("; ");
            sb.Append(prop.Metadata.Name).Append(": \"")
              .Append(Truncate(prop.OriginalValue?.ToString() ?? "", 200)).Append("\" → \"")
              .Append(Truncate(prop.CurrentValue?.ToString() ?? "", 200)).Append('"');
        }
        return sb.ToString();
    }

    private static string DisplayNameOf(Entity entity) => entity switch
    {
        Site s => s.Name,
        Building b => b.Name,
        LinkCategory c => c.Name,
        LinkItem l => l.Title,
        SiteMap m => m.Name,
        MapLayer l => l.Name,
        MapFeature f => f.Label ?? $"#{f.Id}",
        MapLink l => $"Alignment #{l.Id}",
        MapPhoto p => p.FileName,
        MapLegendEntry l => l.Name,
        Vendor v => v.Name,
        Order o => o.Item,
        OrderStatusChange c => $"Order #{c.OrderId}: {OrderStages.Name(c.Status, c.Contact)}",
        SiteStaffMember s => s.Name,
        WikiSection s => s.Name,
        WikiPage p => p.Title,
        WikiImage i => i.FileName,
        RepairListItem i => i.Name,
        Device d => d.AssetTag,
        Repair r => $"Repair #{r.Id}",
        MapFeaturePlacement p => $"Item #{p.MapFeatureId} on alignment #{p.MapLinkId}",
        _ => $"#{entity.Id}"
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

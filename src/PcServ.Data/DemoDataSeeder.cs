using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;

namespace PcServ.Data;

/// <summary>
/// Fills an empty portal with realistic, entirely fictional data for demos.
/// No real district, staff or student information is used.
/// </summary>
public static class DemoDataSeeder
{
    /// <summary>True when there are no sites and no links yet, so demo data won't mix with real data.</summary>
    public static async Task<bool> CanSeedAsync(PcServDbContext db, CancellationToken ct = default) =>
        !await db.Sites.AnyAsync(ct) && !await db.LinkCategories.AnyAsync(ct);

    public static async Task<bool> SeedAsync(PcServDbContext db, CancellationToken ct = default)
    {
        if (!await CanSeedAsync(db, ct)) return false;

        db.Sites.AddRange(Sites());
        db.LinkCategories.AddRange(LinkCategories());
        await db.SaveChangesAsync(ct);

        db.LogAudit("DemoData.Loaded", "System", null, "Demo sites and links were loaded.");
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Removes what <see cref="SeedAsync"/> added. Sites must match on name and address, because a real
    /// site can share a demo site's name (every district has a "District Office"). Links must match on
    /// title and address, and categories are only removed once empty, so anything people added or changed is kept.
    /// </summary>
    public static async Task<(int Sites, int Links)> RemoveAsync(PcServDbContext db, CancellationToken ct = default)
    {
        var siteIds = await DemoSiteIdsAsync(db, ct);
        var sites = await db.Sites.Where(s => siteIds.Contains(s.Id)).ToListAsync(ct);
        db.Orders.RemoveRange(await db.Orders.Where(o => siteIds.Contains(o.SiteId)).ToListAsync(ct));
        db.Repairs.RemoveRange(await db.Repairs.Where(r => siteIds.Contains(r.SiteId)).ToListAsync(ct));
        db.Devices.RemoveRange(await db.Devices.Where(d => d.SiteId != null && siteIds.Contains(d.SiteId.Value) && d.Repairs.All(r => siteIds.Contains(r.SiteId))).ToListAsync(ct));
        db.Sites.RemoveRange(sites);

        var demoCategories = LinkCategories().ToList();
        var categoryNames = demoCategories.Select(c => c.Name).ToList();
        var demoLinks = demoCategories.SelectMany(c => c.Links).Select(l => (l.Title, l.Url)).ToHashSet();
        var categories = await db.LinkCategories.Include(c => c.Links).Where(c => categoryNames.Contains(c.Name)).ToListAsync(ct);

        var removedLinks = 0;
        foreach (var category in categories)
        {
            foreach (var link in category.Links.Where(l => demoLinks.Contains((l.Title, l.Url))).ToList())
            {
                db.Links.Remove(link);
                category.Links.Remove(link);
                removedLinks++;
            }
            if (category.Links.Count == 0) db.LinkCategories.Remove(category);
        }

        db.SuppressEntityAudit = true;
        db.LogAudit("DemoData.Removed", "System", null, $"Removed demo data: {sites.Count} site(s) and {removedLinks} link(s).");
        await db.SaveChangesAsync(ct);
        return (sites.Count, removedLinks);
    }

    /// <summary>The sites in this database that are still demo sites, matched on name and address.</summary>
    public static async Task<List<int>> DemoSiteIdsAsync(PcServDbContext db, CancellationToken ct = default)
    {
        var demo = Sites().Select(s => (s.Name, s.Address)).ToHashSet();
        var names = demo.Select(d => d.Name).ToList();
        var candidates = await db.Sites.AsNoTracking().Where(s => names.Contains(s.Name))
            .Select(s => new { s.Id, s.Name, s.Address }).ToListAsync(ct);
        return candidates.Where(s => demo.Contains((s.Name, s.Address))).Select(s => s.Id).ToList();
    }

    private static IEnumerable<Site> Sites()
    {
        var order = 0;
        Site S(string name, string code, SiteType type, string address, params (string Name, int Floors)[] buildings) => new()
        {
            Name = name,
            Code = code,
            Type = type,
            Address = address,
            MainPhone = $"(555) 01{order:00}-{1000 + order * 37}",
            SortOrder = order++,
            Buildings = buildings.Select((b, i) => new Building { Name = b.Name, Floors = b.Floors, SortOrder = i }).ToList(),
        };

        yield return S("Lincoln Elementary", "LES", SiteType.Elementary, "1200 Lincoln Ave",
            ("Main Building", 1), ("Portables", 1));
        yield return S("Maple Grove Elementary", "MGE", SiteType.Elementary, "455 Maple Grove Rd",
            ("Main Building", 2));
        yield return S("Riverside Elementary", "RES", SiteType.Elementary, "78 River Bend Dr",
            ("Main Building", 1), ("Kindergarten Wing", 1), ("Multipurpose Room", 1));
        yield return S("Cedar Ridge Middle School", "CRM", SiteType.Middle, "3100 Cedar Ridge Pkwy",
            ("Main Building", 2), ("Gymnasium", 1), ("Science Wing", 2));
        yield return S("Westbrook Middle School", "WMS", SiteType.Middle, "900 Westbrook Blvd",
            ("Main Building", 2), ("Band & Choir", 1));
        yield return S("Northview High School", "NVHS", SiteType.High, "5000 Northview Dr",
            ("Main Building", 3), ("Performing Arts Center", 2), ("Field House", 1), ("CTE Building", 1));
        yield return S("District Office", "DO", SiteType.Office, "100 Education Way",
            ("Administration", 2));
        yield return S("Technology Warehouse", "WH", SiteType.Warehouse, "22 Industrial Ct",
            ("Warehouse", 1));
    }

    private static IEnumerable<LinkCategory> LinkCategories()
    {
        var catOrder = 0;
        LinkCategory C(string name, string description, params (string Title, string Url, string Description, bool Pinned)[] links) => new()
        {
            Name = name,
            Description = description,
            SortOrder = catOrder++,
            Links = links.Select((l, i) => new LinkItem
            {
                Title = l.Title, Url = l.Url, Description = l.Description, IsPinned = l.Pinned, SortOrder = i
            }).ToList(),
        };

        yield return C("Admin Consoles", "Management portals for accounts and devices.",
            ("Google Admin Console", "https://admin.google.com", "Users, OUs and ChromeOS device management.", true),
            ("Microsoft 365 Admin Center", "https://admin.microsoft.com", "M365 users, licenses and settings.", true),
            ("Microsoft Entra Admin Center", "https://entra.microsoft.com", "Identity, groups and sign-in logs.", false));

        yield return C("Chromebooks", "Help for ChromeOS devices.",
            ("ChromeOS Device Management Help", "https://support.google.com/chrome/a", "Official Google help for managed Chromebooks.", false),
            ("Powerwash & Recovery", "https://support.google.com/chromebook/answer/1080595", "How to recover a Chromebook's operating system.", true),
            ("Auto Update Policy (AUE dates)", "https://support.google.com/chrome/a/answer/6220366", "Check when a model stops getting updates.", false));

        yield return C("Windows & SCCM", "Configuration Manager and Windows references.",
            ("Configuration Manager Docs", "https://learn.microsoft.com/mem/configmgr/", "Official SCCM / ConfigMgr documentation.", false),
            ("Windows Release Health", "https://learn.microsoft.com/windows/release-health/", "Known issues and update status.", false));

        yield return C("Vendor Warranty & Support", "Look up warranties and open repair cases.",
            ("Dell Support", "https://www.dell.com/support/home", "Warranty lookup by service tag.", true),
            ("Lenovo PC Support", "https://pcsupport.lenovo.com", "Warranty lookup by serial number.", false),
            ("HP Support", "https://support.hp.com", "Warranty and drivers.", false),
            ("Acer Support", "https://www.acer.com/us-en/support", "Chromebook parts and warranty.", false));

        yield return C("Internal (Examples)", "Placeholders. Replace these with your district's real internal links.",
            ("Help Desk Tickets", "https://helpdesk.example.org", "Example: your ticketing system.", true),
            ("Team Wiki", "https://wiki.example.org", "Example: coming in a later phase.", false));
    }
}

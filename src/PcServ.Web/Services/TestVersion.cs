using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services.Maps;

namespace PcServ.Web.Services;

/// <summary>
/// "Show test version": points one Admin's account at a separate database of made-up data, for demos and for
/// trying things safely. Everyone else stays on the real data. Accounts always come from the real database.
/// </summary>
public sealed class TestVersion(string connectionString, string databasePath, SettingsService settings, IDbContextFactory<PcServDbContext> realFactory, IServiceProvider services)
{
    // The account tables depend on the app's Identity settings, so this has to see them too, or its tables wouldn't match.
    private readonly DbContextOptions<PcServDbContext> _options = new DbContextOptionsBuilder<PcServDbContext>()
        .UseSqlite(connectionString).UseApplicationServiceProvider(services).Options;

    public async Task<bool> IsOnAsync(ClaimsPrincipal user)
    {
        // Only Admins, so an account that loses the role goes straight back to real data.
        if (!user.IsInRole(Roles.Admin) || user.Identity?.Name is not { } name) return false;
        return (await UsersAsync()).Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A database connection for this person: the test database if they've turned it on, otherwise the real one.</summary>
    public async Task<PcServDbContext> CreateForAsync(ClaimsPrincipal user) =>
        await IsOnAsync(user) ? new PcServDbContext(_options) : await realFactory.CreateDbContextAsync();

    public async Task SetAsync(ClaimsPrincipal user, bool on)
    {
        if (!user.IsInRole(Roles.Admin)) throw new InvalidOperationException("Only Admins can use the test version.");
        var name = user.Identity?.Name ?? throw new InvalidOperationException("You need to be signed in.");

        var users = await UsersAsync();
        users.RemoveAll(u => string.Equals(u, name, StringComparison.OrdinalIgnoreCase));
        if (on) users.Add(name);

        // Saved with the real settings, so the audit log there shows who was looking at test data, and when.
        await settings.SetAsync(SettingKeys.TestVersionUsers, string.Join(',', users), name);
    }

    public async Task PrepareDatabaseAsync()
    {
        await using var db = new PcServDbContext(_options);
        await db.Database.MigrateAsync();
    }

    /// <summary>Brings an existing test database up to date after an upgrade. It's only created when someone first uses it.</summary>
    public async Task UpgradeIfPresentAsync()
    {
        if (File.Exists(databasePath)) await PrepareDatabaseAsync();
    }

    public void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task<List<string>> UsersAsync() =>
        ((await settings.GetAsync(SettingKeys.TestVersionUsers)) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>Turns the test version on and off, and fills it with fictional content the first time it's used.</summary>
public sealed class TestVersionManager(
    TestVersion testVersion, AppDbFactory dbFactory, SampleMapService sampleMaps, VendorService vendors, SiteStaffService staff,
    OrderService orders, RepairService repairs, RepairListService lists, WikiService wiki, MapFileStore mapFiles, WikiImageStore wikiImages)
{
    public async Task TurnOnAsync(ClaimsPrincipal user)
    {
        await testVersion.PrepareDatabaseAsync();
        await testVersion.SetAsync(user, true);
        await SeedIfEmptyAsync(user);
    }

    public Task TurnOffAsync(ClaimsPrincipal user) => testVersion.SetAsync(user, false);

    /// <summary>Throws the test data away and starts again. It can't reach real data, because this only runs on the test database.</summary>
    public async Task ResetAsync(ClaimsPrincipal user)
    {
        if (!await testVersion.IsOnAsync(user)) throw new InvalidOperationException("Turn on the test version first.");

        await using (var db = await dbFactory.CreateAsync())
        {
            foreach (var key in await db.SiteMaps.Select(m => m.StorageKey).ToListAsync()) mapFiles.Delete(key);
            foreach (var key in await db.WikiImages.Select(i => i.StorageKey).ToListAsync()) wikiImages.Delete(key);
        }
        testVersion.DeleteDatabase();
        await testVersion.PrepareDatabaseAsync();
        await SeedIfEmptyAsync(user);
    }

    private async Task SeedIfEmptyAsync(ClaimsPrincipal user)
    {
        await using (var db = await dbFactory.CreateAsync())
        {
            if (!await DemoDataSeeder.SeedAsync(db)) return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var map = (await sampleMaps.AddAsync())!;

        int siteId, otherSiteId;
        await using (var db = await dbFactory.CreateAsync())
        {
            siteId = await db.Sites.Where(s => s.Name == SampleMapService.SampleSiteName).Select(s => s.Id).FirstAsync();
            otherSiteId = await db.Sites.Where(s => s.Id != siteId).OrderBy(s => s.Id).Select(s => s.Id).FirstAsync();
        }

        foreach (var (name, website) in new[] { ("Example Supply Co", "https://www.example.com"), ("Sample Tech Distributors", "https://www.example.org") })
        {
            await vendors.SaveAsync(new Vendor { Name = name, Website = website }, user);
        }
        var vendor = (await vendors.ListAsync(approvedOnly: true)).First();

        await staff.SaveAsync(new SiteStaffMember { SiteId = siteId, Name = "Alex Rivera", JobTitle = "Office Manager", IsOrderContact = true }, user);
        await staff.SaveAsync(new SiteStaffMember { SiteId = siteId, Name = "Sam Taylor", JobTitle = "Teacher", Room = "Rm 7" }, user);

        // Orders, at different stages and ages, so the Overview has something to show.
        var waiting = await orders.CreateAsync(new OrderEdit("27-inch monitor", 2, siteId, "Sam Taylor", "Rm 7", vendor.Id, null, "Q-1042", null, null, "Alex Rivera"), today.AddDays(-24), user);
        await orders.SetStatusAsync(waiting, OrderStatus.SentToContact, today.AddDays(-19), "Quote sent for approval.", user);
        await orders.PlaceOnMapAsync(waiting, map.MapId, .72, .42, user);
        var shipped = await orders.CreateAsync(new OrderEdit("Classroom projector", 1, siteId, null, "Library", vendor.Id, null, "Q-1038", "1Z-EXAMPLE", null, "Alex Rivera"), today.AddDays(-30), user);
        await orders.SetStatusAsync(shipped, OrderStatus.Ordered, today.AddDays(-12), null, user);
        await orders.SetStatusAsync(shipped, OrderStatus.Shipped, today.AddDays(-3), null, user);
        await orders.CreateAsync(new OrderEdit("Replacement keyboards", 10, otherSiteId, null, null, null, "Catalog example", null, null, null), today.AddDays(-2), user);

        // Repairs. The asset tags are made up, and don't look like anyone's real ones.
        foreach (var name in new[] { "Example Chromebook A", "Example Chromebook B" }) await lists.SaveAsync(new RepairListItem { Kind = RepairListKind.Model, Name = name }, user);
        var model = (await lists.ListAsync(RepairListKind.Model)).First().Id;
        var reasons = await lists.ListAsync(RepairListKind.Reason);
        async Task<int> Repair(string tag, string serial, int reason, int daysAgo, string pickup, int site)
        {
            return await repairs.CheckInAsync(new RepairCheckIn(tag, serial, model, site, reasons[reason].Id, null, pickup, today.AddDays(-daysAgo)), user);
        }
        await Repair("0000000101", "TESTSERIAL101", 0, 16, "Cart 3", siteId);
        var parts = await Repair("0000000102", "TESTSERIAL102", 2, 11, "Cart 3", siteId);
        await repairs.SetStatusAsync(parts, RepairStatus.OnHoldForParts, today, user);
        await Repair("0000000103", "TESTSERIAL103", 6, 3, "Library", otherSiteId);
        var done = await Repair("0000000104", "TESTSERIAL104", 7, 9, "Rm 7", siteId);
        await repairs.SetStatusAsync(done, RepairStatus.Completed, today.AddDays(-1), user);
        var retired = await Repair("0000000105", "TESTSERIAL105", 0, 20, "Cart 1", siteId);
        await repairs.SetStatusAsync(retired, RepairStatus.BeyondRepair, today.AddDays(-2), user);

        // The wiki.
        var section = (await wiki.TreeAsync(user)).First().Id;
        foreach (var (title, html, role) in new[]
        {
            ("Welcome to the test version", "<p>Everything here is made up. Change anything you like, because it can't affect real records.</p><h2>Things to try</h2><ul><li><p>Check in a device under Repairs.</p></li><li><p>Move an order along under Orders.</p></li><li><p>Open the sample site map, and switch its layers on and off.</p></li></ul>", Roles.Viewer),
            ("Setting up a classroom printer", "<h2>Before you start</h2><p>Check that the printer is on the same network as the classroom.</p><div data-callout=\"note\"><p>This page is sample content.</p></div><h2>Find the printer</h2><pre><code>Get-Printer</code></pre>", Roles.Viewer),
            ("Network closet notes", "<p>An example of a page that only Techs and above can read.</p>", Roles.Tech),
        })
        {
            var id = await wiki.CreatePageAsync(section, title, role, user);
            await wiki.PublishAsync(id, title, html, 0, false, user);
        }
    }
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;
using UglyToad.PdfPig;

namespace PcServ.Tests;

public class OrderTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Tech = UserInRole(Roles.Tech);
    private static readonly ClaimsPrincipal Manager = UserInRole(Roles.Manager);
    private static readonly ClaimsPrincipal Viewer = UserInRole(Roles.Viewer);
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static OrderService Orders(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<OrderService>();
    private static VendorService Vendors(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<VendorService>();
    private static SiteMapService Maps(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<SiteMapService>();

    private static async Task<int> AddSiteAsync(AsyncServiceScope scope, string name = "Test School") =>
        (await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = name + " " + Guid.NewGuid() })).Id;

    private static async Task<int> AddMapAsync(AsyncServiceScope scope, int siteId) =>
        (await Maps(scope).CreateFromUploadAsync(new MapUpload(siteId, null, "Site plan", SiteMapKind.SitePlan, null, Guid.NewGuid() + ".pdf", TestPdf.Build(0, 800, 600)))).MapId;

    private static OrderEdit Edit(int siteId, string item = "Monitor", string? other = "Local Shop", int? vendorId = null, string? forWhom = "Sam Teacher") =>
        new(item, 1, siteId, forWhom, "12", vendorId, other, null, null, null, "Pat Contact");

    [Fact]
    public async Task A_new_order_starts_at_requested_with_its_first_history_entry()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);

        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);

        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Equal(OrderStatus.Requested, order.Status);
        Assert.Equal(Today, order.StatusDate);
        Assert.Equal("Pat Contact", order.Contact);
        var entry = Assert.Single(order.History);
        Assert.Equal(OrderStatus.Requested, entry.Status);
        Assert.False(string.IsNullOrEmpty(entry.By));
    }

    [Fact]
    public async Task Stage_changes_keep_their_dates_notes_and_order()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Orders(scope).CreateAsync(Edit(await AddSiteAsync(scope)), Today.AddDays(-30), Tech);
        var sentOn = Today.AddDays(-20);

        await Orders(scope).SetStatusAsync(id, OrderStatus.SentToContact, sentOn, " Quote 123 ", Tech);
        await Orders(scope).SetStatusAsync(id, OrderStatus.Received, Today, null, Tech);

        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Equal(OrderStatus.Received, order.Status);
        Assert.Equal([OrderStatus.Received, OrderStatus.SentToContact, OrderStatus.Requested], order.History.Select(h => h.Status));
        var sent = order.History.Single(h => h.Status == OrderStatus.SentToContact);
        Assert.Equal(sentOn, sent.Date);
        Assert.Equal("Quote 123", sent.Note);
    }

    [Fact]
    public async Task Same_stage_and_future_dates_are_refused_and_going_back_is_recorded()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Orders(scope).CreateAsync(Edit(await AddSiteAsync(scope)), Today, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).SetStatusAsync(id, OrderStatus.Requested, Today, null, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).SetStatusAsync(id, OrderStatus.Ordered, Today.AddDays(5), null, Tech));

        await Orders(scope).SetStatusAsync(id, OrderStatus.Ordered, Today, null, Tech);
        await Orders(scope).SetStatusAsync(id, OrderStatus.Requested, Today, "Sent by mistake", Tech);

        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Equal(OrderStatus.Requested, order.Status);
        Assert.Equal(3, order.History.Count);
    }

    [Fact]
    public void Shipped_is_on_the_way_and_finished_orders_have_no_next_stage()
    {
        Assert.Equal(OrderStatus.SentToContact, OrderStages.Next(OrderStatus.Requested));
        Assert.Null(OrderStages.Next(OrderStatus.Received));
        Assert.Null(OrderStages.Next(OrderStatus.Cancelled));
        Assert.True(OrderStages.IsOpen(OrderStatus.Shipped));
        Assert.False(OrderStages.IsOpen(OrderStatus.Received));
        Assert.False(OrderStages.IsOpen(OrderStatus.Cancelled));
    }

    [Fact]
    public async Task Viewers_cannot_read_or_change_orders()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).ListAsync(Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).GetAsync(id, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).CreateAsync(Edit(siteId), Today, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).SetStatusAsync(id, OrderStatus.Ordered, Today, null, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).RemoveFromMapAsync(id, Viewer));
    }

    [Theory]
    [InlineData("", "Shop")]
    [InlineData("   ", "Shop")]
    [InlineData("Monitor", "")]
    [InlineData("Monitor", null)]
    public async Task Orders_need_an_item_and_a_vendor(string item, string? other)
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).CreateAsync(Edit(siteId, item, other), Today, Tech));
    }

    [Fact]
    public async Task Orders_need_a_real_site_and_quantity()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).CreateAsync(Edit(99999), Today, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).CreateAsync(Edit(siteId) with { Quantity = 0 }, Today, Tech));
    }

    [Fact]
    public async Task Only_managers_change_the_vendor_list()
    {
        await using var scope = _app.ScopeAs("admin");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Vendors(scope).SaveAsync(new Vendor { Name = "Acme" }, Tech));
        await Vendors(scope).SaveAsync(new Vendor { Name = " Acme ", Website = "https://example.com" }, Manager);

        Assert.Equal("Acme", (await Vendors(scope).ListAsync()).Single().Name);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("example.com")]
    public async Task Vendor_websites_must_be_ordinary_web_addresses(string website)
    {
        await using var scope = _app.ScopeAs("admin");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Vendors(scope).SaveAsync(new Vendor { Name = "Acme", Website = website }, Manager));
    }

    [Fact]
    public async Task Vendor_names_are_unique()
    {
        await using var scope = _app.ScopeAs("admin");
        await Vendors(scope).SaveAsync(new Vendor { Name = "Acme" }, Manager);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Vendors(scope).SaveAsync(new Vendor { Name = "ACME" }, Manager));
    }

    [Fact]
    public async Task A_vendor_losing_approval_leaves_new_orders_but_stays_on_old_ones()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        await Vendors(scope).SaveAsync(new Vendor { Name = "Acme" }, Manager);
        var vendor = (await Vendors(scope).ListAsync()).Single();
        var id = await Orders(scope).CreateAsync(Edit(siteId, other: null, vendorId: vendor.Id), Today, Tech);

        vendor.IsApproved = false;
        await Vendors(scope).SaveAsync(vendor, Manager);

        Assert.Empty(await Vendors(scope).ListAsync(approvedOnly: true));
        Assert.Equal("Acme", (await Orders(scope).GetAsync(id, Tech))!.VendorName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).CreateAsync(Edit(siteId, other: null, vendorId: vendor.Id), Today, Tech));
        await Orders(scope).UpdateAsync(id, Edit(siteId, "Changed", other: null, vendorId: vendor.Id), Tech);
        Assert.Equal("Changed", (await Orders(scope).GetAsync(id, Tech))!.Item);
    }

    [Fact]
    public async Task Other_vendors_are_marked_and_replacing_with_an_approved_one_clears_the_name()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        await Vendors(scope).SaveAsync(new Vendor { Name = "Acme" }, Manager);
        var vendor = (await Vendors(scope).ListAsync()).Single();
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);
        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.True(order.IsOtherVendor);
        Assert.Equal("Local Shop", order.VendorName);

        await Orders(scope).UpdateAsync(id, Edit(siteId, other: "Ignored", vendorId: vendor.Id), Tech);

        order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.False(order.IsOtherVendor);
        Assert.Equal("Acme", order.VendorName);
    }

    [Fact]
    public async Task A_pin_shows_the_live_stage_and_goes_away_when_the_order_is_finished()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var mapId = await AddMapAsync(scope, siteId);
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);

        var (layer, pin) = await Orders(scope).PlaceOnMapAsync(id, mapId, .3, .4, Tech);
        Assert.Equal("Monitor for Sam Teacher: Requested", pin.Label);
        Assert.Equal(id, pin.OrderId);

        async Task<MapFeatureView?> PinOnMap() => (await Maps(scope).GetAsync(mapId, Tech))!.Layers.Single(l => l.Id == layer.Id).Features.SingleOrDefault();

        await Orders(scope).SetStatusAsync(id, OrderStatus.Shipped, Today, null, Tech);
        Assert.Equal("Monitor for Sam Teacher: Shipped", (await PinOnMap())!.Label);

        await Orders(scope).SetStatusAsync(id, OrderStatus.Received, Today, null, Tech);
        Assert.Null(await PinOnMap());
        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Equal(mapId, order.MapId);

        // Reopening brings it back, in the same place.
        await Orders(scope).SetStatusAsync(id, OrderStatus.Shipped, Today, "Wrong item, sent back", Tech);
        Assert.Equal(.3, (await PinOnMap())!.X, 6);
    }

    [Fact]
    public async Task Pins_are_restricted_and_that_cannot_be_changed()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var mapId = await AddMapAsync(scope, siteId);
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);
        var (layer, _) = await Orders(scope).PlaceOnMapAsync(id, mapId, .3, .4, Tech);

        Assert.Equal(Roles.Tech, layer.MinimumRole);
        Assert.DoesNotContain((await Maps(scope).GetAsync(mapId, Viewer))!.Layers, l => l.Id == layer.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Maps(scope).SetLayerRoleAsync(layer.Id, Roles.Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<MapLayerService>().DeleteLayerAsync(layer.Id, Tech));
    }

    [Fact]
    public async Task An_order_can_only_be_pinned_on_its_own_site_while_open()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var otherMap = await AddMapAsync(scope, await AddSiteAsync(scope, "Other"));
        var mapId = await AddMapAsync(scope, siteId);
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).PlaceOnMapAsync(id, otherMap, .3, .4, Tech));

        await Orders(scope).SetStatusAsync(id, OrderStatus.Cancelled, Today, null, Tech);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(scope).PlaceOnMapAsync(id, mapId, .3, .4, Tech));
    }

    [Fact]
    public async Task Pinning_again_replaces_the_pin_and_removing_keeps_the_order()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var mapId = await AddMapAsync(scope, siteId);
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();

        await Orders(scope).PlaceOnMapAsync(id, mapId, .3, .4, Tech);
        await Orders(scope).PlaceOnMapAsync(id, mapId, .6, .7, Tech);
        Assert.Equal(1, await db.MapFeatures.CountAsync(f => f.Symbol == MapSymbols.OrderPin));

        await Orders(scope).RemoveFromMapAsync(id, Tech);

        Assert.False(await db.MapFeatures.AnyAsync(f => f.Symbol == MapSymbols.OrderPin));
        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Null(order.MapId);
        Assert.Equal("Monitor", order.Item);
    }

    [Fact]
    public async Task Moving_an_order_to_another_site_removes_its_pin()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var mapId = await AddMapAsync(scope, siteId);
        var other = await AddSiteAsync(scope, "Other");
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);
        await Orders(scope).PlaceOnMapAsync(id, mapId, .3, .4, Tech);

        await Orders(scope).UpdateAsync(id, Edit(other), Tech);

        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Equal(other, order.SiteId);
        Assert.Null(order.MapId);
    }

    [Fact]
    public async Task Deleting_a_map_keeps_its_orders()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var mapId = await AddMapAsync(scope, siteId);
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);
        await Orders(scope).PlaceOnMapAsync(id, mapId, .3, .4, Tech);

        await Maps(scope).DeleteAsync(mapId);

        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Null(order.MapId);
    }

    [Fact]
    public async Task A_site_with_orders_cannot_be_deleted_by_accident()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);

        Assert.Equal(1, await Orders(scope).CountForSiteAsync(siteId));
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<AppDbFactory>().DeleteAsync<Site>(siteId));
    }

    [Fact]
    public async Task Removing_demo_data_removes_orders_on_demo_sites_only()
    {
        await using var scope = _app.ScopeAs("admin");
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        await DemoDataSeeder.SeedAsync(db);
        var demoSite = (await DemoDataSeeder.DemoSiteIdsAsync(db)).First();
        var realSite = await AddSiteAsync(scope, "Real");
        await Orders(scope).CreateAsync(Edit(demoSite, "Demo order"), Today, Tech);
        var kept = await Orders(scope).CreateAsync(Edit(realSite, "Real order"), Today, Tech);

        await DemoDataSeeder.RemoveAsync(db);

        var left = await Orders(scope).ListAsync(Tech);
        Assert.Equal(kept, Assert.Single(left).Id);
    }

    [Fact]
    public async Task Pins_are_in_the_pdf_with_their_stage()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var mapId = await AddMapAsync(scope, siteId);
        var id = await Orders(scope).CreateAsync(Edit(siteId), Today, Tech);
        var (layer, _) = await Orders(scope).PlaceOnMapAsync(id, mapId, .3, .4, Tech);

        var pdf = await scope.ServiceProvider.GetRequiredService<MapExportService>().CreatePdfAsync(mapId, new HashSet<int> { layer.Id }, MapArea.Whole, "today", Tech);

        using var document = PdfDocument.Open(pdf!.Content);
        var text = string.Concat(document.GetPage(1).Letters.Select(l => l.Value));
        Assert.Contains("Monitor for Sam Teacher: Requested", text);
        Assert.Contains("Ordered item", text);
        Assert.Contains("Contains restricted information", text);
    }

    [Fact]
    public async Task Order_changes_are_in_the_audit_log()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Orders(scope).CreateAsync(Edit(await AddSiteAsync(scope)), Today, Tech);
        await Orders(scope).SetStatusAsync(id, OrderStatus.Ordered, Today, null, Tech);

        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == nameof(Order) && a.Action == "Created"));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == nameof(Order) && a.Action == "Updated"));
    }

    public void Dispose() => _app.Dispose();
}

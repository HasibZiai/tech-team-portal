using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;

namespace PcServ.Tests;

public class RepairTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "pat"), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Tech = UserInRole(Roles.Tech);
    private static readonly ClaimsPrincipal Manager = UserInRole(Roles.Manager);
    private static readonly ClaimsPrincipal Viewer = UserInRole(Roles.Viewer);
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static RepairService Repairs(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<RepairService>();
    private static RepairListService Lists(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<RepairListService>();

    private static async Task<int> AddSiteAsync(AsyncServiceScope scope) =>
        (await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "School " + Guid.NewGuid() })).Id;

    private static async Task<int> FirstAsync(AsyncServiceScope scope, RepairListKind kind)
    {
        // Reasons come with the app. Models depend on what each organization owns, so tests add their own.
        if (kind == RepairListKind.Model && (await Lists(scope).ListAsync(kind)).Count == 0)
        {
            await Lists(scope).SaveAsync(new RepairListItem { Kind = kind, Name = "Test Model" }, Manager);
        }
        return (await Lists(scope).ListAsync(kind)).First().Id;
    }

    private static async Task<RepairCheckIn> EntryAsync(AsyncServiceScope scope, string tag = "A100", string? serial = "SN100", int? siteId = null) =>
        new(tag, serial, await FirstAsync(scope, RepairListKind.Model), siteId ?? await AddSiteAsync(scope), await FirstAsync(scope, RepairListKind.Reason), null, "Cart 3", Today);

    [Theory]
    [InlineData("the asset system", "The asset system", "Asset system")]
    [InlineData("Some Product", "Some Product", "Some Product")]
    public void Names_read_properly_in_sentences_and_labels(string name, string sentence, string label)
    {
        Assert.Equal(sentence, DistrictSettings.Sentence(name));
        Assert.Equal(label, DistrictSettings.Label(name));
    }

    [Fact]
    public void Names_are_generic_unless_set()
    {
        var settings = Settings(null);
        Assert.Equal(("the asset system", "the device console"), (settings.InventoryName, settings.DeviceConsoleName));
        Assert.Null(settings.InventoryUrl);
        Assert.Null(settings.DeviceConsoleUrl);
    }

    [Theory]
    [InlineData(" https://inventory.example.org/ ", "https://inventory.example.org/")]
    [InlineData("http://inventory.local", "http://inventory.local")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,hello", null)]
    [InlineData("inventory.example.org", null)]
    public void The_inventory_address_must_be_an_ordinary_web_address(string? value, string? expected) =>
        Assert.Equal(expected, DistrictSettings.WebAddressOrNull(value));

    private static DistrictSettings Settings(string? digits) => new(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["PcServ:District:AssetTagDigits"] = digits }).Build());

    [Theory]
    [InlineData("10", "1234567890", true)]
    [InlineData("10", "123456789", false)]
    [InlineData("10", "12345678901", false)]
    [InlineData("10", "5CD123ABCD", false)]
    [InlineData("10", "١٢٣٤٥٦٧٨٩٠", false)]
    [InlineData(null, "5CD123ABCD", true)]
    [InlineData("nonsense", "5CD123ABCD", true)]
    [InlineData("0", "5CD123ABCD", true)]
    [InlineData(null, "  ", false)]
    public void The_scanner_only_takes_what_could_be_an_asset_tag(string? digits, string code, bool expected) =>
        Assert.Equal(expected, Settings(digits).CouldBeAssetTag(code));

    [Fact]
    public async Task The_lists_start_with_the_usual_reasons_and_no_models()
    {
        await using var scope = _app.ScopeAs("admin");

        var models = (await Lists(scope).ListAsync(RepairListKind.Model)).Select(m => m.Name).ToList();
        var reasons = (await Lists(scope).ListAsync(RepairListKind.Reason)).Select(r => r.Name).ToList();

        Assert.Empty(models);
        Assert.Contains("Broken/cracked screen", reasons);
        Assert.Equal(10, reasons.Count);
    }

    [Fact]
    public async Task Checking_in_records_the_device_and_starts_in_for_repairs()
    {
        await using var scope = _app.ScopeAs("admin");

        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);

        var repair = (await Repairs(scope).GetAsync(id, Tech))!;
        Assert.Equal(("A100", "SN100", RepairStatus.InRepair, "Cart 3"), (repair.AssetTag, repair.SerialNumber, repair.Status, repair.PickupPoint));
        Assert.Equal(Today, repair.DateIn);
        Assert.Null(repair.DateOut);
        Assert.False(repair.InventoryIsCurrent);
    }

    [Fact]
    public async Task A_known_asset_tag_fills_in_whatever_case_it_is_typed_in()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope);
        await Repairs(scope).CheckInAsync(entry, Tech);

        var known = (await Repairs(scope).LookUpAsync(" a100 ", Tech))!;

        Assert.Equal(("A100", "SN100", entry.ModelId, entry.SiteId), (known.AssetTag, known.SerialNumber, known.ModelId, (int?)known.SiteId));
        Assert.NotNull(known.OpenRepairId);
        Assert.Null(await Repairs(scope).LookUpAsync("NEW", Tech));
        Assert.Null(await Repairs(scope).LookUpAsync("  ", Tech));
    }

    [Fact]
    public async Task A_device_can_only_have_one_open_repair()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope);
        var first = await Repairs(scope).CheckInAsync(entry, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry with { AssetTag = "a100" }, Tech));

        await Repairs(scope).SetStatusAsync(first, RepairStatus.Completed, Today, Tech);
        var second = await Repairs(scope).CheckInAsync(entry, Tech);
        Assert.NotEqual(first, second);
        Assert.Equal(2, (await Repairs(scope).HistoryAsync((await Repairs(scope).GetAsync(second, Tech))!.DeviceId, Tech)).Count);

        // Reopening the old one would make two open repairs.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).SetStatusAsync(first, RepairStatus.InRepair, Today, Tech));
    }

    [Fact]
    public async Task Finishing_fills_in_the_date_out_and_reopening_clears_it()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);

        await Repairs(scope).SetStatusAsync(id, RepairStatus.Completed, Today, Tech);
        Assert.Equal(Today, (await Repairs(scope).GetAsync(id, Tech))!.DateOut);

        await Repairs(scope).SetStatusAsync(id, RepairStatus.InRepair, Today, Tech);
        Assert.Null((await Repairs(scope).GetAsync(id, Tech))!.DateOut);

        await Repairs(scope).SetStatusAsync(id, RepairStatus.BeyondRepair, Today, Tech);
        Assert.Equal(Today, (await Repairs(scope).GetAsync(id, Tech))!.DateOut);
    }

    [Fact]
    public async Task Moving_on_hold_does_not_finish_the_repair()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);

        await Repairs(scope).SetStatusAsync(id, RepairStatus.OnHoldForParts, Today, Tech);

        Assert.Null((await Repairs(scope).GetAsync(id, Tech))!.DateOut);
    }

    [Fact]
    public async Task Inventory_is_flagged_again_when_the_status_changes()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);

        await Repairs(scope).MarkInventoryUpdatedAsync([id], Today, Tech);
        var repair = (await Repairs(scope).GetAsync(id, Tech))!;
        Assert.True(repair.InventoryIsCurrent);
        Assert.Equal((RepairStatus.InRepair, Today, "pat"), (repair.InventoryUpdatedTo!.Value, repair.InventoryUpdatedOn!.Value, repair.InventoryUpdatedBy!));

        await Repairs(scope).SetStatusAsync(id, RepairStatus.Completed, Today, Tech);
        repair = (await Repairs(scope).GetAsync(id, Tech))!;
        Assert.False(repair.InventoryIsCurrent);
        Assert.Equal(RepairStatus.InRepair, repair.InventoryUpdatedTo);

        await Repairs(scope).MarkInventoryUpdatedAsync([id], Today, Tech);
        await Repairs(scope).ClearInventoryUpdatedAsync(id, Tech);
        Assert.Null((await Repairs(scope).GetAsync(id, Tech))!.InventoryUpdatedTo);
    }

    [Fact]
    public void Barcode_lists_have_one_tag_per_line_and_no_repeats()
    {
        RepairView Row(int id, string tag) => new(id, id, tag, null, null, null, 1, "S", null, "", null, null, RepairStatus.InRepair, Today, null, null, null, null, null, false, false);

        Assert.Equal("A1\r\nB2\r\n", RepairService.BarcodeList([Row(1, "A1"), Row(2, "B2"), Row(3, "a1")]));
    }

    [Fact]
    public async Task Other_needs_a_description()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope) with { ReasonId = null, OtherReason = "  " };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry, Tech));

        var id = await Repairs(scope).CheckInAsync(entry with { OtherReason = "Sticky hinge" }, Tech);
        Assert.Equal("Sticky hinge", (await Repairs(scope).GetAsync(id, Tech))!.ReasonText);
    }

    [Fact]
    public async Task Bad_entries_are_refused()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry with { AssetTag = "  " }, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry with { DateIn = Today.AddDays(5) }, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry with { SiteId = 99999 }, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry with { ReasonId = entry.ModelId }, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry with { ModelId = entry.ReasonId }, Tech));
    }

    [Fact]
    public async Task A_serial_number_cannot_belong_to_two_devices()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope);
        await Repairs(scope).CheckInAsync(entry, Tech);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry with { AssetTag = "B200" }, Tech));

        Assert.Contains("A100", error.Message);
        Assert.False(await scope.ServiceProvider.GetRequiredService<PcServDbContext>().Devices.AnyAsync(d => d.AssetTag == "B200"));
    }

    [Fact]
    public async Task Editing_saves_details_and_the_date_out_cannot_come_first()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope);
        var id = await Repairs(scope).CheckInAsync(entry, Tech);
        RepairEdit Edit(RepairStatus status, DateOnly? dateOut) => new("SN-NEW", entry.ModelId, entry.SiteId, entry.ReasonId, null, " Library ", status, Today, dateOut, " Replaced screen ", true, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).UpdateAsync(id, Edit(RepairStatus.Completed, Today.AddDays(-3)), Today, Tech));

        await Repairs(scope).UpdateAsync(id, Edit(RepairStatus.Completed, null), Today, Tech);
        var repair = (await Repairs(scope).GetAsync(id, Tech))!;
        Assert.Equal(("SN-NEW", "Library", "Replaced screen", Today), (repair.SerialNumber, repair.PickupPoint, repair.Notes, repair.DateOut!.Value));
        Assert.True(repair.FineNeeded && repair.FineDone);

        await Repairs(scope).UpdateAsync(id, Edit(RepairStatus.InRepair, Today) with { FineNeeded = false }, Today, Tech);
        repair = (await Repairs(scope).GetAsync(id, Tech))!;
        Assert.Null(repair.DateOut);
        Assert.False(repair.FineDone);
    }

    [Fact]
    public async Task A_late_check_in_date_cannot_make_the_date_out_come_first()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);

        // Finished on an earlier "today" than the date in, as could happen across time zones.
        await Repairs(scope).SetStatusAsync(id, RepairStatus.Completed, Today.AddDays(-2), Tech);

        Assert.Equal(Today, (await Repairs(scope).GetAsync(id, Tech))!.DateOut);
    }

    [Fact]
    public async Task Cant_be_repaired_puts_a_device_out_of_circulation_until_it_is_checked_in_again()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope);
        var first = await Repairs(scope).CheckInAsync(entry, Tech);
        Assert.Empty(RepairService.OutOfCirculation(await Repairs(scope).ListAsync(Tech)));

        await Repairs(scope).SetStatusAsync(first, RepairStatus.BeyondRepair, Today, Tech);
        Assert.Equal(first, Assert.Single(RepairService.OutOfCirculation(await Repairs(scope).ListAsync(Tech))).Id);
        Assert.True((await Repairs(scope).LookUpAsync("A100", Tech))!.OutOfCirculation);

        await Repairs(scope).CheckInAsync(entry, Tech);
        Assert.Empty(RepairService.OutOfCirculation(await Repairs(scope).ListAsync(Tech)));
    }

    [Fact]
    public async Task Completed_devices_are_not_out_of_circulation()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);

        await Repairs(scope).SetStatusAsync(id, RepairStatus.Completed, Today, Tech);

        Assert.Empty(RepairService.OutOfCirculation(await Repairs(scope).ListAsync(Tech)));
    }

    [Fact]
    public async Task Deprovisioning_is_recorded_on_the_device_and_can_be_undone()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);
        await Repairs(scope).SetStatusAsync(id, RepairStatus.BeyondRepair, Today, Tech);
        var deviceId = (await Repairs(scope).GetAsync(id, Tech))!.DeviceId;

        await Repairs(scope).MarkDeprovisionedAsync(deviceId, Today, Tech);
        var repair = (await Repairs(scope).GetAsync(id, Tech))!;
        Assert.Equal((Today, "pat"), (repair.DeprovisionedOn!.Value, repair.DeprovisionedBy!));

        await Repairs(scope).ClearDeprovisionedAsync(deviceId, Tech);
        Assert.Null((await Repairs(scope).GetAsync(id, Tech))!.DeprovisionedOn);
    }

    [Fact]
    public async Task A_deprovisioned_device_that_comes_back_needs_deprovisioning_again_next_time()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope);
        var id = await Repairs(scope).CheckInAsync(entry, Tech);
        await Repairs(scope).SetStatusAsync(id, RepairStatus.BeyondRepair, Today, Tech);
        await Repairs(scope).MarkDeprovisionedAsync((await Repairs(scope).GetAsync(id, Tech))!.DeviceId, Today, Tech);
        Assert.NotNull((await Repairs(scope).LookUpAsync("A100", Tech))!.DeprovisionedOn);

        var again = await Repairs(scope).CheckInAsync(entry, Tech);
        Assert.Null((await Repairs(scope).GetAsync(again, Tech))!.DeprovisionedOn);

        await Repairs(scope).SetStatusAsync(again, RepairStatus.BeyondRepair, Today, Tech);
        var repair = Assert.Single(RepairService.OutOfCirculation(await Repairs(scope).ListAsync(Tech)));
        Assert.Null(repair.DeprovisionedOn);
    }

    [Fact]
    public async Task Reopening_a_deprovisioned_repair_clears_it()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);
        await Repairs(scope).SetStatusAsync(id, RepairStatus.BeyondRepair, Today, Tech);
        await Repairs(scope).MarkDeprovisionedAsync((await Repairs(scope).GetAsync(id, Tech))!.DeviceId, Today, Tech);

        await Repairs(scope).SetStatusAsync(id, RepairStatus.InRepair, Today, Tech);

        Assert.Null((await Repairs(scope).GetAsync(id, Tech))!.DeprovisionedOn);
    }

    [Fact]
    public async Task Viewers_cannot_mark_devices_as_deprovisioned()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);
        var deviceId = (await Repairs(scope).GetAsync(id, Tech))!.DeviceId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).MarkDeprovisionedAsync(deviceId, Today, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).ClearDeprovisionedAsync(deviceId, Viewer));
    }

    [Fact]
    public async Task Viewers_cannot_reach_repairs()
    {
        await using var scope = _app.ScopeAs("admin");
        var entry = await EntryAsync(scope);
        var id = await Repairs(scope).CheckInAsync(entry, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).ListAsync(Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).GetAsync(id, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).LookUpAsync("A100", Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).CheckInAsync(entry, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).SetStatusAsync(id, RepairStatus.Completed, Today, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repairs(scope).MarkInventoryUpdatedAsync([id], Today, Viewer));
    }

    [Fact]
    public async Task Only_managers_change_the_lists_and_retired_entries_stay_on_past_repairs()
    {
        await using var scope = _app.ScopeAs("admin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Lists(scope).SaveAsync(new RepairListItem { Kind = RepairListKind.Model, Name = "New" }, Tech));
        await Lists(scope).SaveAsync(new RepairListItem { Kind = RepairListKind.Model, Name = "Existing Model" }, Manager);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Lists(scope).SaveAsync(new RepairListItem { Kind = RepairListKind.Model, Name = "existing model" }, Manager));
        await Lists(scope).SaveAsync(new RepairListItem { Kind = RepairListKind.Model, Name = " Lenovo 100e " }, Manager);
        var added = (await Lists(scope).ListAsync(RepairListKind.Model)).Single(m => m.Name == "Lenovo 100e");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope) with { ModelId = added.Id }, Tech);

        added.IsActive = false;
        await Lists(scope).SaveAsync(added, Manager);

        Assert.DoesNotContain(await Lists(scope).ListAsync(RepairListKind.Model, activeOnly: true), m => m.Id == added.Id);
        Assert.Equal("Lenovo 100e", (await Repairs(scope).GetAsync(id, Tech))!.ModelName);
    }

    [Fact]
    public async Task A_site_with_repairs_cannot_be_deleted_by_accident()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        await Repairs(scope).CheckInAsync(await EntryAsync(scope, siteId: siteId), Tech);

        Assert.Equal(1, await Repairs(scope).CountForSiteAsync(siteId));
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<AppDbFactory>().DeleteAsync<Site>(siteId));
    }

    [Fact]
    public async Task Removing_demo_data_removes_repairs_on_demo_sites_only()
    {
        await using var scope = _app.ScopeAs("admin");
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        await DemoDataSeeder.SeedAsync(db);
        var demoSite = (await DemoDataSeeder.DemoSiteIdsAsync(db)).First();
        await Repairs(scope).CheckInAsync(await EntryAsync(scope, "DEMO", "SN-DEMO", demoSite), Tech);
        var kept = await Repairs(scope).CheckInAsync(await EntryAsync(scope, "REAL", "SN-REAL"), Tech);

        await DemoDataSeeder.RemoveAsync(db);

        Assert.Equal(kept, Assert.Single(await Repairs(scope).ListAsync(Tech)).Id);
    }

    [Fact]
    public async Task Repairs_are_in_the_audit_log()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Repairs(scope).CheckInAsync(await EntryAsync(scope), Tech);
        await Repairs(scope).SetStatusAsync(id, RepairStatus.Completed, Today, Tech);

        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == nameof(Repair) && a.Action == "Created"));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == nameof(Repair) && a.Action == "Updated"));
    }

    public void Dispose() => _app.Dispose();
}

using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;

namespace PcServ.Tests;

public class SiteStaffTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Tech = UserInRole(Roles.Tech);
    private static readonly ClaimsPrincipal Viewer = UserInRole(Roles.Viewer);
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static SiteStaffService Staff(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<SiteStaffService>();
    private static OrderService Orders(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<OrderService>();

    private static async Task<int> AddSiteAsync(AsyncServiceScope scope) =>
        (await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "School " + Guid.NewGuid() })).Id;

    [Fact]
    public async Task Techs_keep_the_list_and_viewers_cannot_see_it()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);

        await Staff(scope).SaveAsync(new SiteStaffMember { SiteId = siteId, Name = " Alex Rivera ", Room = " 12 " }, Tech);

        var member = Assert.Single(await Staff(scope).ListAsync(siteId, Tech));
        Assert.Equal(("Alex Rivera", "12"), (member.Name, member.Room));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Staff(scope).ListAsync(siteId, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Staff(scope).SaveAsync(new SiteStaffMember { SiteId = siteId, Name = "X" }, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Staff(scope).DeleteAsync(member.Id, Viewer));
    }

    [Fact]
    public async Task Names_are_required_and_unique_within_a_site_only()
    {
        await using var scope = _app.ScopeAs("admin");
        int first = await AddSiteAsync(scope), second = await AddSiteAsync(scope);
        await Staff(scope).SaveAsync(new SiteStaffMember { SiteId = first, Name = "Alex Rivera" }, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Staff(scope).SaveAsync(new SiteStaffMember { SiteId = first, Name = "  " }, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Staff(scope).SaveAsync(new SiteStaffMember { SiteId = first, Name = "ALEX RIVERA" }, Tech));
        await Staff(scope).SaveAsync(new SiteStaffMember { SiteId = second, Name = "Alex Rivera" }, Tech);

        Assert.Single(await Staff(scope).ListAsync(second, Tech));
    }

    [Fact]
    public async Task A_site_has_one_usual_contact_and_other_sites_are_unaffected()
    {
        await using var scope = _app.ScopeAs("admin");
        int first = await AddSiteAsync(scope), second = await AddSiteAsync(scope);
        await Staff(scope).SaveAsync(new SiteStaffMember { SiteId = first, Name = "Alex", IsOrderContact = true }, Tech);
        await Staff(scope).SaveAsync(new SiteStaffMember { SiteId = second, Name = "Other", IsOrderContact = true }, Tech);

        await Staff(scope).SaveAsync(new SiteStaffMember { SiteId = first, Name = "Blair", IsOrderContact = true }, Tech);

        Assert.Equal("Blair", (await Staff(scope).ListAsync(first, Tech)).Single(s => s.IsOrderContact).Name);
        Assert.Equal("Other", (await Staff(scope).ListAsync(second, Tech)).Single(s => s.IsOrderContact).Name);
    }

    [Fact]
    public async Task Removing_someone_leaves_orders_that_name_them()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        await Staff(scope).SaveAsync(new SiteStaffMember { SiteId = siteId, Name = "Alex" }, Tech);
        var id = await Orders(scope).CreateAsync(new OrderEdit("Monitor", 1, siteId, "Alex", null, null, "Shop", null, null, null, "Alex"), Today, Tech);

        await Staff(scope).DeleteAsync((await Staff(scope).ListAsync(siteId, Tech)).Single().Id, Tech);

        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Equal(("Alex", "Alex"), (order.RequestedFor, order.Contact));
    }

    [Fact]
    public void The_stage_names_the_contact_when_there_is_one()
    {
        Assert.Equal("Sent to Alex", OrderStages.Name(OrderStatus.SentToContact, "Alex"));
        Assert.Equal("Sent to contact", OrderStages.Name(OrderStatus.SentToContact));
        Assert.Equal("Sent to contact", OrderStages.Name(OrderStatus.SentToContact, "  "));
        Assert.Equal("Ordered", OrderStages.Name(OrderStatus.Ordered, "Alex"));
    }

    [Fact]
    public async Task A_name_not_on_the_list_is_fine_and_history_keeps_who_it_went_to()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var id = await Orders(scope).CreateAsync(new OrderEdit("Monitor", 1, siteId, null, null, null, "Shop", null, null, null), Today, Tech);

        await Orders(scope).SetStatusAsync(id, OrderStatus.SentToContact, Today, null, Tech, " Not On List ");
        await Orders(scope).SetStatusAsync(id, OrderStatus.Requested, Today, "Wrong person", Tech);
        await Orders(scope).SetStatusAsync(id, OrderStatus.SentToContact, Today, null, Tech, "Blair");

        var order = (await Orders(scope).GetAsync(id, Tech))!;
        Assert.Equal("Blair", order.Contact);
        Assert.Equal(["Blair", "Not On List"], order.History.Where(h => h.Status == OrderStatus.SentToContact).Select(h => h.Contact));
    }

    [Fact]
    public async Task Moving_to_the_stage_without_a_new_name_keeps_the_orders_contact()
    {
        await using var scope = _app.ScopeAs("admin");
        var siteId = await AddSiteAsync(scope);
        var id = await Orders(scope).CreateAsync(new OrderEdit("Monitor", 1, siteId, null, null, null, "Shop", null, null, null, "Alex"), Today, Tech);

        await Orders(scope).SetStatusAsync(id, OrderStatus.SentToContact, Today, null, Tech);

        Assert.Equal("Alex", (await Orders(scope).GetAsync(id, Tech))!.History.First().Contact);
    }

    [Fact]
    public async Task Upgrading_moves_finance_contacts_to_the_staff_list_and_renames_the_stage()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pcserv-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PcServDbContext>().UseSqlite($"Data Source={file}")
                // This deliberately stops at an older version, which the current model doesn't match.
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            await using (var db = new PcServDbContext(options))
            {
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrator.MigrateAsync("AddOrders");

                const string Stamp = "'2026-11-01 00:00:00', 'test'";
                await db.Database.ExecuteSqlRawAsync($"INSERT INTO Sites (Id, Name, Type, IsActive, SortOrder, FinanceContact, CreatedAt, CreatedBy) VALUES (1, 'With contact', 'Elementary', 1, 0, 'Alex Rivera', {Stamp}), (2, 'Without', 'Elementary', 1, 0, NULL, {Stamp}), (3, 'Blank', 'Elementary', 1, 0, '  ', {Stamp})");
                await db.Database.ExecuteSqlRawAsync($"INSERT INTO Orders (Id, Item, Quantity, SiteId, OtherVendor, Status, StatusDate, CreatedAt, CreatedBy) VALUES (1, 'Sent one', 1, 1, 'Shop', 'SentToFinance', '2026-11-01', {Stamp}), (2, 'New one', 1, 2, 'Shop', 'Requested', '2026-11-01', {Stamp})");
                await db.Database.ExecuteSqlRawAsync($"INSERT INTO OrderStatusChanges (OrderId, Status, Date, CreatedAt, CreatedBy) VALUES (1, 'Requested', '2026-10-30', {Stamp}), (1, 'SentToFinance', '2026-11-01', {Stamp})");

                await migrator.MigrateAsync();
            }

            await using (var db = new PcServDbContext(options))
            {
                var member = await db.SiteStaffMembers.SingleAsync();
                Assert.Equal((1, "Alex Rivera", true), (member.SiteId, member.Name, member.IsOrderContact));

                var sent = await db.Orders.Include(o => o.History).SingleAsync(o => o.Id == 1);
                Assert.Equal(OrderStatus.SentToContact, sent.Status);
                Assert.Equal("Alex Rivera", sent.Contact);
                Assert.Equal("Alex Rivera", sent.History.Single(h => h.Status == OrderStatus.SentToContact).Contact);
                Assert.Null(sent.History.Single(h => h.Status == OrderStatus.Requested).Contact);
                Assert.Null((await db.Orders.SingleAsync(o => o.Id == 2)).Contact);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    public void Dispose() => _app.Dispose();
}

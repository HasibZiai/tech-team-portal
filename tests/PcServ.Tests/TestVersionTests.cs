using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;

namespace PcServ.Tests;

public class TestVersionTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal Admin(string name = "admin") => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, Roles.Admin)], "test"));

    private AsyncServiceScope Scope(string name = "admin") => _app.ScopeAs(name, Roles.Admin);

    private static T Get<T>(AsyncServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    private static async Task<List<string>> SiteNames(AsyncServiceScope scope)
    {
        await using var db = await Get<AppDbFactory>(scope).CreateAsync();
        return await db.Sites.Select(s => s.Name).ToListAsync();
    }

    [Fact]
    public async Task Turning_it_on_shows_made_up_data_and_hides_real_data_and_turning_it_off_brings_it_back()
    {
        await using var scope = Scope();
        await Get<AppDbFactory>(scope).SaveAsync(new Site { Name = "Real School" });

        await Get<TestVersionManager>(scope).TurnOnAsync(Admin());

        var names = await SiteNames(scope);
        Assert.DoesNotContain("Real School", names);
        Assert.Contains(SampleMapService.SampleSiteName, names);

        await Get<TestVersionManager>(scope).TurnOffAsync(Admin());
        Assert.Equal(["Real School"], await SiteNames(scope));
    }

    [Fact]
    public async Task Changes_in_the_test_version_never_reach_real_data()
    {
        await using var scope = Scope();
        await Get<TestVersionManager>(scope).TurnOnAsync(Admin());

        await Get<AppDbFactory>(scope).SaveAsync(new Site { Name = "Made up in test" });

        var real = await Get<IDbContextFactory<PcServDbContext>>(scope).CreateDbContextAsync();
        await using (real)
        {
            Assert.Empty(await real.Sites.ToListAsync());
            Assert.Empty(await real.Repairs.ToListAsync());
            Assert.Empty(await real.Orders.ToListAsync());
            Assert.Empty(await real.WikiPages.ToListAsync());
        }
    }

    [Fact]
    public async Task It_is_per_person_so_other_people_stay_on_real_data()
    {
        await using var on = Scope();
        await Get<AppDbFactory>(on).SaveAsync(new Site { Name = "Real School" });
        await Get<TestVersionManager>(on).TurnOnAsync(Admin());

        await using var otherAdmin = Scope("other");
        await using var tech = _app.ScopeAs("admin-named-tech", Roles.Tech);

        Assert.Equal(["Real School"], await SiteNames(otherAdmin));
        Assert.Equal(["Real School"], await SiteNames(tech));
    }

    [Fact]
    public async Task Someone_who_loses_the_admin_role_goes_back_to_real_data()
    {
        await using var scope = Scope();
        await Get<AppDbFactory>(scope).SaveAsync(new Site { Name = "Real School" });
        await Get<TestVersionManager>(scope).TurnOnAsync(Admin());

        await using var demoted = _app.ScopeAs("admin", Roles.Tech);

        Assert.Equal(["Real School"], await SiteNames(demoted));
    }

    [Fact]
    public async Task Only_admins_can_turn_it_on()
    {
        await using var scope = _app.ScopeAs("pat", Roles.Manager);
        var manager = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "pat"), new Claim(ClaimTypes.Role, Roles.Manager)], "test"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Get<TestVersionManager>(scope).TurnOnAsync(manager));
    }

    [Fact]
    public async Task It_has_something_to_show_in_every_area()
    {
        await using var scope = Scope();
        await Get<TestVersionManager>(scope).TurnOnAsync(Admin());

        var data = await Get<OverviewService>(scope).GetAsync(Admin());

        Assert.True(data.Work!.OpenRepairs > 0);
        Assert.True(data.Work.OpenOrders > 0);
        Assert.True(data.Work.ToDeprovision > 0);
        Assert.True(data.Work.InventoryUpdates > 0);
        Assert.True(data.Maps > 0);
        Assert.True(data.WikiPages > 0);
        Assert.True(data.Sites > 0);
        Assert.Contains(await Get<OrderService>(scope).ListAsync(Admin()), o => o.MapId is not null);
    }

    [Fact]
    public async Task Turning_it_on_again_keeps_changes_and_resetting_starts_over()
    {
        await using var scope = Scope();
        await Get<TestVersionManager>(scope).TurnOnAsync(Admin());
        await Get<AppDbFactory>(scope).SaveAsync(new Site { Name = "Made up in test" });
        await Get<TestVersionManager>(scope).TurnOffAsync(Admin());

        await Get<TestVersionManager>(scope).TurnOnAsync(Admin());
        Assert.Contains("Made up in test", await SiteNames(scope));

        await Get<TestVersionManager>(scope).ResetAsync(Admin());
        var names = await SiteNames(scope);
        Assert.DoesNotContain("Made up in test", names);
        Assert.Contains(SampleMapService.SampleSiteName, names);
    }

    [Fact]
    public async Task Resetting_removes_test_files_and_leaves_real_ones()
    {
        await using var scope = Scope();
        var real = await Get<AppDbFactory>(scope).SaveAsync(new Site { Name = "Real School" });
        var realMap = (await Get<SiteMapService>(scope).CreateFromUploadAsync(new MapUpload(real.Id, null, "Plan", SiteMapKind.SitePlan, null, "plan.pdf", TestPdf.Build(0, 800, 600)))).MapId;
        var realImage = await Get<SiteMapService>(scope).BaseImagePathAsync(realMap, Admin());

        await Get<TestVersionManager>(scope).TurnOnAsync(Admin());
        var testMap = (await Get<SiteMapService>(scope).ListAsync(Admin())).First().Id;
        var testImage = await Get<SiteMapService>(scope).BaseImagePathAsync(testMap, Admin());
        Assert.NotEqual(realImage, testImage);

        await Get<TestVersionManager>(scope).ResetAsync(Admin());

        Assert.True(File.Exists(realImage));
        Assert.False(File.Exists(testImage));
    }

    [Fact]
    public async Task Resetting_refuses_to_run_on_real_data()
    {
        await using var scope = Scope();
        await Get<AppDbFactory>(scope).SaveAsync(new Site { Name = "Real School" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Get<TestVersionManager>(scope).ResetAsync(Admin()));

        Assert.Equal(["Real School"], await SiteNames(scope));
    }

    [Fact]
    public async Task Turning_it_on_is_recorded_in_the_real_audit_log()
    {
        await using var scope = Scope();
        await Get<TestVersionManager>(scope).TurnOnAsync(Admin());

        await using var real = await Get<IDbContextFactory<PcServDbContext>>(scope).CreateDbContextAsync();
        Assert.True(await real.AuditEntries.AnyAsync(a => a.Summary.Contains(SettingKeys.TestVersionUsers)));
    }

    public void Dispose() => _app.Dispose();
}

public class OverviewTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "pat"), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static async Task<(OverviewService Overview, RepairService Repairs, OrderService Orders, int SiteId, int ReasonId)> SetUpAsync(AsyncServiceScope scope)
    {
        var site = await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "School" });
        var reason = (await scope.ServiceProvider.GetRequiredService<RepairListService>().ListAsync(RepairListKind.Reason)).First().Id;
        return (scope.ServiceProvider.GetRequiredService<OverviewService>(), scope.ServiceProvider.GetRequiredService<RepairService>(), scope.ServiceProvider.GetRequiredService<OrderService>(), site.Id, reason);
    }

    [Fact]
    public async Task Viewers_get_no_repair_or_order_information()
    {
        await using var scope = _app.ScopeAs("admin");
        var (overview, repairs, _, site, reason) = await SetUpAsync(scope);
        await repairs.CheckInAsync(new RepairCheckIn("A1", null, null, site, reason, null, null, Today), UserInRole(Roles.Tech));

        var data = await overview.GetAsync(UserInRole(Roles.Viewer));

        Assert.Null(data.Work);
        Assert.Equal(1, data.Sites);
    }

    [Fact]
    public async Task Alignments_only_show_for_managers()
    {
        await using var scope = _app.ScopeAs("admin");
        var (overview, _, _, _, _) = await SetUpAsync(scope);

        Assert.Null((await overview.GetAsync(UserInRole(Roles.Tech))).Work!.AlignmentsToCheck);
        Assert.Equal(0, (await overview.GetAsync(UserInRole(Roles.Manager))).Work!.AlignmentsToCheck);
    }

    [Fact]
    public async Task Counts_follow_the_work_and_lists_put_the_longest_waiting_first()
    {
        await using var scope = _app.ScopeAs("admin");
        var (overview, repairs, orders, site, reason) = await SetUpAsync(scope);
        var tech = UserInRole(Roles.Tech);
        await repairs.CheckInAsync(new RepairCheckIn("NEW", null, null, site, reason, null, null, Today), tech);
        await repairs.CheckInAsync(new RepairCheckIn("OLD", null, null, site, reason, null, null, Today.AddDays(-9)), tech);
        var finished = await repairs.CheckInAsync(new RepairCheckIn("DONE", null, null, site, reason, null, null, Today.AddDays(-20)), tech);
        await repairs.SetStatusAsync(finished, RepairStatus.BeyondRepair, Today, tech);
        await orders.CreateAsync(new OrderEdit("Newer", 1, site, null, null, null, "Shop", null, null, null), Today, tech);
        await orders.CreateAsync(new OrderEdit("Older", 1, site, null, null, null, "Shop", null, null, null), Today.AddDays(-5), tech);
        var received = await orders.CreateAsync(new OrderEdit("Received", 1, site, null, null, null, "Shop", null, null, null), Today.AddDays(-30), tech);
        await orders.SetStatusAsync(received, OrderStatus.Received, Today, null, tech);

        var work = (await overview.GetAsync(tech)).Work!;

        Assert.Equal((2, 2, 3, 1), (work.OpenRepairs, work.OpenOrders, work.InventoryUpdates, work.ToDeprovision));
        Assert.Equal(["OLD", "NEW"], work.OldestRepairs.Select(r => r.AssetTag));
        Assert.Equal(["Older", "Newer"], work.OldestOrders.Select(o => o.Item));
    }

    public void Dispose() => _app.Dispose();
}

public class AccountRecoveryTests : IDisposable
{
    private readonly TestApp _app = new();

    private async Task<ApplicationUser> AddUserAsync(string name, string role)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = name, DisplayName = name + " Person", LockoutEnabled = true };
        await users.CreateAsync(user, "OldPassword12345");
        await users.AddToRoleAsync(user, role);
        return user;
    }

    private async Task<HttpResponseMessage> SignInAsync(string name, string password) =>
        await TestApp.PostFormAsync(_app.CreateBrowser(), "/Account/Login", "login", new() { ["Input.UserName"] = name, ["Input.Password"] = password });

    [Fact]
    public async Task The_temporary_password_works_and_must_be_changed()
    {
        await AddUserAsync("boss", Roles.Admin);

        var recovered = (await AccountRecovery.ResetAsync(_app.Services, "BOSS"))!;

        Assert.Equal("boss", recovered.UserName);
        var response = await SignInAsync("boss", recovered.TemporaryPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = _app.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync("boss");
        Assert.True(user!.MustChangePassword);
        Assert.False(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CheckPasswordAsync(user, "OldPassword12345"));
    }

    [Fact]
    public async Task It_clears_lockouts_disabled_accounts_and_two_factor()
    {
        var added = await AddUserAsync("boss", Roles.Admin);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(added.Id))!;
            user.IsDisabled = true;
            user.LockoutEnd = DateTimeOffset.MaxValue;
            await users.UpdateAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        }

        var recovered = (await AccountRecovery.ResetAsync(_app.Services, "boss"))!;

        Assert.True(recovered.WasDisabled);
        Assert.True(recovered.HadTwoFactor);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var user = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync("boss"))!;
            Assert.False(user.IsDisabled);
            Assert.Null(user.LockoutEnd);
            Assert.False(user.TwoFactorEnabled);
        }
    }

    [Fact]
    public async Task An_unknown_account_changes_nothing()
    {
        Assert.Null(await AccountRecovery.ResetAsync(_app.Services, "nobody"));
        Assert.Equal(1, await AccountRecovery.RunAsync(_app.Services, [AccountRecovery.ResetFlag, "nobody"]));
        Assert.Equal(1, await AccountRecovery.RunAsync(_app.Services, [AccountRecovery.ResetFlag]));
    }

    [Fact]
    public async Task Admins_can_be_listed_without_showing_anyone_else()
    {
        await AddUserAsync("boss", Roles.Admin);
        await AddUserAsync("tech1", Roles.Tech);

        var list = await AccountRecovery.ListAdminsAsync(_app.Services);

        Assert.Contains("boss (boss Person)", Assert.Single(list));
    }

    [Fact]
    public async Task It_is_in_the_audit_log_as_coming_from_the_server()
    {
        await AddUserAsync("boss", Roles.Admin);

        await AccountRecovery.ResetAsync(_app.Services, "boss");

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        var entry = await db.AuditEntries.FirstAsync(a => a.Action == "User.PasswordReset");
        Assert.Equal("server command", entry.UserName);
        Assert.DoesNotContain("Temporary password:", entry.Summary);
    }

    public void Dispose() => _app.Dispose();
}

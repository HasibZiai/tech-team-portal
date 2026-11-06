using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;

namespace PcServ.Tests;

public class AuditTests : IDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task Saving_records_stamps_the_user_and_writes_the_audit_log()
    {
        await using (var scope = _app.ScopeAs("jsmith"))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
            var site = await db.SaveAsync(new Site { Name = "Lincoln Elementary", Code = "LES" });
            Assert.NotEqual(0, site.Id);
            Assert.Equal("jsmith", site.CreatedBy);

            // Edit a copy, like the edit dialog does.
            var edited = site.Copy<Site>();
            edited.Code = "LINC";
            await db.SaveAsync(edited);
        }

        await using var check = _app.Services.CreateAsyncScope();
        var ctx = check.ServiceProvider.GetRequiredService<PcServDbContext>();
        var entries = await ctx.AuditEntries.Where(a => a.EntityType == nameof(Site)).OrderBy(a => a.Id).ToListAsync();

        Assert.Collection(entries,
            created => { Assert.Equal("Created", created.Action); Assert.Equal("jsmith", created.UserName); },
            updated =>
            {
                Assert.Equal("Updated", updated.Action);
                Assert.Contains("Code: \"LES\" → \"LINC\"", updated.Summary);
                Assert.DoesNotContain("Name", updated.Summary);
            });

        var saved = await ctx.Sites.SingleAsync();
        Assert.Equal("jsmith", saved.UpdatedBy);
    }

    [Fact]
    public async Task Demo_data_loads_once_only()
    {
        await using var scope = _app.ScopeAs("admin");
        var db = await scope.ServiceProvider.GetRequiredService<AppDbFactory>().CreateAsync();

        Assert.True(await DemoDataSeeder.SeedAsync(db));
        Assert.False(await DemoDataSeeder.SeedAsync(db));
        Assert.True(await db.Sites.CountAsync() >= 5);
        Assert.True(await db.Links.AnyAsync(l => l.IsPinned));
    }

    public void Dispose() => _app.Dispose();
}

public class UserAdminTests : IDisposable
{
    private readonly TestApp _app = new();

    private async Task<(string AdminId, UserAdminService Service, UserManager<ApplicationUser> Users, AsyncServiceScope Scope)> SetupAsync()
    {
        var scope = _app.ScopeAs("admin");
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = new ApplicationUser { UserName = "admin", DisplayName = "First Admin", LockoutEnabled = true };
        Assert.True((await users.CreateAsync(admin, "AdminPassword1")).Succeeded);
        await users.AddToRoleAsync(admin, Roles.Admin);
        return (admin.Id, scope.ServiceProvider.GetRequiredService<UserAdminService>(), users, scope);
    }

    [Fact]
    public async Task New_users_get_a_working_temporary_password_and_must_change_it()
    {
        var (_, service, users, scope) = await SetupAsync();
        await using var _ = scope;

        var temp = await service.CreateAsync("jsmith", "Jordan Smith", Roles.Tech);

        var user = await users.FindByNameAsync("jsmith");
        Assert.NotNull(user);
        Assert.True(user.MustChangePassword);
        Assert.True(await users.CheckPasswordAsync(user, temp));
        Assert.Equal([Roles.Tech], await users.GetRolesAsync(user));
    }

    [Fact]
    public async Task Duplicate_usernames_are_rejected()
    {
        var (_, service, _, scope) = await SetupAsync();
        await using var _ = scope;

        await service.CreateAsync("jsmith", "Jordan Smith", Roles.Tech);
        await Assert.ThrowsAsync<UserAdminException>(() => service.CreateAsync("jsmith", "Someone Else", Roles.Viewer));
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted_or_disabled()
    {
        var (adminId, service, _, scope) = await SetupAsync();
        await using var _ = scope;
        await service.CreateAsync("second", "Second Admin", Roles.Admin);
        var secondId = (await service.ListAsync()).Single(u => u.UserName == "second").Id;

        // Nobody can demote or disable themselves.
        await Assert.ThrowsAsync<UserAdminException>(() => service.UpdateAsync(adminId, "First Admin", Roles.Tech, adminId));
        await Assert.ThrowsAsync<UserAdminException>(() => service.SetDisabledAsync(adminId, true, adminId));

        // With another admin around, the second admin can be demoted...
        await service.UpdateAsync(secondId, "Second Admin", Roles.Manager, adminId);

        // ...but then the first admin is the last one, so the second can't demote them.
        await Assert.ThrowsAsync<UserAdminException>(() => service.UpdateAsync(adminId, "First Admin", Roles.Viewer, secondId));
    }

    [Fact]
    public async Task Disabled_users_cannot_sign_in()
    {
        var (adminId, service, users, scope) = await SetupAsync();
        await using var _ = scope;
        var temp = await service.CreateAsync("jsmith", "Jordan Smith", Roles.Tech);
        var id = (await users.FindByNameAsync("jsmith"))!.Id;

        await service.SetDisabledAsync(id, true, adminId);

        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(id))!;
        var result = await signIn.CheckPasswordSignInAsync(user, temp, lockoutOnFailure: false);
        Assert.True(result.IsLockedOut);

        await service.SetDisabledAsync(id, false, adminId);
        user = (await users.FindByIdAsync(id))!;
        Assert.True((await signIn.CheckPasswordSignInAsync(user, temp, lockoutOnFailure: false)).Succeeded);
    }

    public void Dispose() => _app.Dispose();
}

public class SignInFlowTests : IDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task A_fresh_install_sends_everyone_to_setup()
    {
        var browser = _app.CreateBrowser();

        var response = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/setup", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Users_with_a_temporary_password_must_change_it_first()
    {
        string temp;
        await using (var scope = _app.ScopeAs("admin"))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = new ApplicationUser { UserName = "admin", DisplayName = "Admin" };
            await users.CreateAsync(admin, "AdminPassword1");
            await users.AddToRoleAsync(admin, Roles.Admin);
            temp = await scope.ServiceProvider.GetRequiredService<UserAdminService>().CreateAsync("newtech", "New Tech", Roles.Tech);
        }

        var browser = _app.CreateBrowser();
        var login = await TestApp.PostFormAsync(browser, "/Account/Login", "login", new()
        {
            ["Input.UserName"] = "newtech",
            ["Input.Password"] = temp,
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/Account/Manage/ChangePassword", home.Headers.Location?.OriginalString);

        var change = await TestApp.PostFormAsync(browser, "/Account/Manage/ChangePassword", "change-password", new()
        {
            ["Input.OldPassword"] = temp,
            ["Input.NewPassword"] = "MyNewPassword42",
            ["Input.ConfirmPassword"] = "MyNewPassword42",
        });
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);

        var after = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Contains("Hello, New", await after.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewers_cannot_open_manager_pages()
    {
        await using (var scope = _app.ScopeAs("admin"))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = new ApplicationUser { UserName = "viewer", DisplayName = "Vee Viewer" };
            await users.CreateAsync(viewer, "ViewerPassword1");
            await users.AddToRoleAsync(viewer, Roles.Viewer);
        }

        var browser = _app.CreateBrowser();
        var login = await TestApp.PostFormAsync(browser, "/Account/Login", "login", new()
        {
            ["Input.UserName"] = "viewer",
            ["Input.Password"] = "ViewerPassword1",
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var linksResponse = await browser.GetAsync("/links");
        Assert.True(linksResponse.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for /links but got {(int)linksResponse.StatusCode} -> {linksResponse.Headers.Location}");
        var links = await linksResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Edit links", links);

        var manage = await browser.GetAsync("/manage/users");
        Assert.Equal(HttpStatusCode.Redirect, manage.StatusCode);
        var location = manage.Headers.Location!;
        Assert.StartsWith("/Account/AccessDenied", location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString);
    }

    public void Dispose() => _app.Dispose();
}

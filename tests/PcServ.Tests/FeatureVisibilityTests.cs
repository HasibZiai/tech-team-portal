using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Tests;

public class FeatureVisibilityTests : IDisposable
{
    private readonly TestApp _app = new();

    private async Task<HttpClient> SignInAsync(string role)
    {
        var name = role.ToLowerInvariant() + "-user";
        await using (var scope = _app.ScopeAs("admin"))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = name, DisplayName = name };
            await users.CreateAsync(user, "SomePassword123");
            await users.AddToRoleAsync(user, role);
        }

        var browser = _app.CreateBrowser();
        await TestApp.PostFormAsync(browser, "/Account/Login", "login", new() { ["Input.UserName"] = name, ["Input.Password"] = "SomePassword123" });
        return browser;
    }

    [Theory]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.Tech)]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Admin)]
    public async Task The_script_library_is_hidden_from_everyone(string role)
    {
        var browser = await SignInAsync(role);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/scripts")).StatusCode);
        Assert.DoesNotContain("Script Library", await browser.GetStringAsync("/links"));
    }

    [Theory]
    [InlineData(Roles.Tech)]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Admin)]
    public async Task Techs_and_above_are_offered_camera_scanning_marked_experimental(string role)
    {
        var browser = await SignInAsync(role);

        var page = await browser.GetStringAsync("/repairs");

        Assert.Contains("Scan with camera", page);
        Assert.Contains("Experimental", page);
    }

    [Fact]
    public async Task Viewers_cannot_reach_camera_scanning()
    {
        var browser = await SignInAsync(Roles.Viewer);

        var page = await browser.GetAsync("/repairs");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("AccessDenied", page.Headers.Location?.ToString());
    }

    [Fact]
    public async Task The_personality_core_is_off_until_an_admin_turns_it_on()
    {
        var browser = await SignInAsync(Roles.Admin);

        Assert.DoesNotContain("A note from the Personality Core", await browser.GetStringAsync("/about"));
        Assert.Contains("Personality Core", await browser.GetStringAsync("/manage/settings"));
        Assert.DoesNotContain("Personality Core", await browser.GetStringAsync("/manage"));

        await using (var scope = _app.ScopeAs("admin"))
        {
            await scope.ServiceProvider.GetRequiredService<PcServ.Web.Services.SettingsService>().SetAsync(SettingKeys.PersonalityCore, "true", "admin");
        }

        Assert.Contains("A note from the Personality Core", await browser.GetStringAsync("/about"));
    }

    [Theory]
    [InlineData(Roles.Viewer, "/manage/audit")]
    [InlineData(Roles.Viewer, "/manage/vendors")]
    [InlineData(Roles.Viewer, "/repairs")]
    [InlineData(Roles.Viewer, "/orders")]
    [InlineData(Roles.Tech, "/manage")]
    [InlineData(Roles.Tech, "/manage/audit")]
    [InlineData(Roles.Tech, "/manage/repair-lists")]
    [InlineData(Roles.Manager, "/manage/users")]
    [InlineData(Roles.Manager, "/manage/settings")]
    [InlineData(Roles.Manager, "/manage/import")]
    public async Task Pages_above_a_persons_role_are_refused(string role, string address)
    {
        var browser = await SignInAsync(role);

        var page = await browser.GetAsync(address);

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("AccessDenied", page.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/links")]
    [InlineData("/wiki")]
    [InlineData("/maps")]
    [InlineData("/repairs")]
    [InlineData("/manage/users")]
    public async Task Signed_out_people_are_sent_to_sign_in(string address)
    {
        await SignInAsync(Roles.Viewer); // So first-run setup is finished.

        var page = await _app.CreateBrowser().GetAsync(address);

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/Account/Login", page.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript://example.org/%0aalert(1)")]
    [InlineData("data:text/html,hello")]
    public void Links_only_accept_web_addresses(string address)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var link = new LinkItem { Title = "A link", Url = address };

        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(link, new(link), results, validateAllProperties: true));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(LinkItem.Url)));
    }

    [Fact]
    public async Task Pages_send_the_security_headers()
    {
        var page = await _app.CreateBrowser().GetAsync("/Account/Login");

        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", page.Headers.GetValues("Referrer-Policy").Single());
    }

    public void Dispose() => _app.Dispose();
}

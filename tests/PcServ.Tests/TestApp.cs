using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace PcServ.Tests;

/// <summary>
/// Runs the real app in memory against its own throwaway SQLite database.
/// </summary>
public sealed partial class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcserv-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("PcServ:DataDirectory", _dataDirectory);
    }

    /// <summary>
    /// Creates a DI scope that behaves as if <paramref name="userName"/> is signed in,
    /// so services like AppDbFactory stamp the audit log with that name.
    /// </summary>
    public AsyncServiceScope ScopeAs(string userName, params string[] roles)
    {
        var scope = Services.CreateAsyncScope();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, userName), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], "test"));
        var provider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        return scope;
    }

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Loads a server-rendered form page and posts it back with the antiforgery token, like a browser would.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, string formName, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(url);
        var token = TokenRegex().Match(page).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"No antiforgery token found on {url}");

        var form = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
            ["_handler"] = formName,
        };
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { /* SQLite may still hold the file briefly */ }
    }
}

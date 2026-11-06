using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Web.Services;

/// <summary>Adds the display name and "must change password" flag to the sign-in cookie.</summary>
public sealed class AppClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaimTypes.DisplayName, string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? "" : user.DisplayName));
        if (user.MustChangePassword)
        {
            identity.AddClaim(new Claim(AppClaimTypes.MustChangePassword, "true"));
        }
        return identity;
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(AppClaimTypes.DisplayName) ?? user.Identity?.Name ?? "";

    public static string? PrimaryRole(this ClaimsPrincipal user) =>
        Roles.All.Reverse().FirstOrDefault(user.IsInRole);
}

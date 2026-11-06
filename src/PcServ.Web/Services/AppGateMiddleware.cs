using Microsoft.AspNetCore.Identity;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Web.Services;

/// <summary>Remembers whether first-run setup has been completed, so we only check the database once.</summary>
public sealed class SetupState
{
    public bool IsComplete { get; set; }
}

/// <summary>
/// Two simple gates that run before every page:
/// 1. First run: until an administrator exists, every page redirects to /setup.
/// 2. Temporary passwords: users who must change their password are sent to the change-password page.
/// </summary>
public sealed class AppGateMiddleware(RequestDelegate next, SetupState setup)
{
    public const string SetupPath = "/setup";
    public const string ChangePasswordPath = "/Account/Manage/ChangePassword";

    public async Task InvokeAsync(HttpContext context, UserManager<ApplicationUser> userManager)
    {
        var path = context.Request.Path;

        if (IsAlwaysAllowed(path))
        {
            await next(context);
            return;
        }

        if (!setup.IsComplete)
        {
            setup.IsComplete = userManager.Users.Any();
            if (!setup.IsComplete && !path.StartsWithSegments(SetupPath))
            {
                context.Response.Redirect(SetupPath);
                return;
            }
        }

        if (context.User.HasClaim(AppClaimTypes.MustChangePassword, "true")
            && !path.StartsWithSegments(ChangePasswordPath)
            && !path.StartsWithSegments("/Account/Logout"))
        {
            context.Response.Redirect(ChangePasswordPath);
            return;
        }

        await next(context);
    }

    /// <summary>Static files and Blazor's own endpoints are never redirected.</summary>
    private static bool IsAlwaysAllowed(PathString path) =>
        path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/_content")
        || path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/lib")
        || path.StartsWithSegments("/health")
        || Path.HasExtension(path.Value);
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Web.Services;

public sealed record RecoveredAccount(string UserName, string TemporaryPassword, bool WasDisabled, bool HadTwoFactor);

/// <summary>
/// Getting back in when nobody can sign in as an Admin, for example after staff turnover. These run on the server,
/// not over the web, so having access to the server is what proves the right to do this.
///
///   dotnet PcServ.Web.dll --list-admins
///   dotnet PcServ.Web.dll --reset-password username
/// </summary>
public static class AccountRecovery
{
    public const string ListFlag = "--list-admins";
    public const string ResetFlag = "--reset-password";

    public static bool IsRequested(string[] args) => args.Contains(ListFlag) || args.Contains(ResetFlag);

    /// <summary>Does what was asked, and returns the exit code.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (args.Contains(ListFlag))
        {
            var admins = await ListAdminsAsync(services);
            Console.WriteLine();
            Console.WriteLine(admins.Count == 0 ? "There are no Admin accounts." : "Admin accounts:");
            foreach (var line in admins) Console.WriteLine("  " + line);
            return 0;
        }

        var index = Array.IndexOf(args, ResetFlag);
        var userName = index + 1 < args.Length ? args[index + 1] : null;
        Console.WriteLine();
        if (string.IsNullOrWhiteSpace(userName) || userName.StartsWith("--", StringComparison.Ordinal))
        {
            Console.WriteLine($"Say which account, for example: {ResetFlag} username. To see the Admins, use {ListFlag}.");
            return 1;
        }

        var recovered = await ResetAsync(services, userName);
        if (recovered is null)
        {
            Console.WriteLine($"There's no account called \"{userName}\". To see the Admins, use {ListFlag}.");
            return 1;
        }

        Console.WriteLine($"Reset the password for {recovered.UserName}.");
        Console.WriteLine($"Temporary password: {recovered.TemporaryPassword}");
        Console.WriteLine("They'll choose a new one when they sign in.");
        if (recovered.WasDisabled) Console.WriteLine("This account was disabled, and has been turned back on.");
        if (recovered.HadTwoFactor) Console.WriteLine("Two-factor sign-in was turned off for it, in case the phone is gone. It can be set up again.");
        return 0;
    }

    public static async Task<List<string>> ListAdminsAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admins = await users.GetUsersInRoleAsync(Roles.Admin);
        return admins.OrderBy(a => a.UserName)
            .Select(a => $"{a.UserName} ({a.DisplayName}){(a.IsDisabled ? ", disabled" : "")}{(a.LastLoginAt is { } at ? $", last signed in {at.ToLocalTime():MMM d, yyyy}" : ", never signed in")}")
            .ToList();
    }

    /// <summary>Gives an account a temporary password and clears anything else that would stop them getting in. Null if there's no such account.</summary>
    public static async Task<RecoveredAccount?> ResetAsync(IServiceProvider services, string userName)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync(userName.Trim());
        if (user is null) return null;

        var (wasDisabled, hadTwoFactor) = (user.IsDisabled, user.TwoFactorEnabled);
        var password = TempPasswordGenerator.Generate();
        Check(await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password));

        user.MustChangePassword = true;
        user.IsDisabled = false;
        user.LockoutEnabled = true;
        user.LockoutEnd = null;
        Check(await users.UpdateAsync(user));
        await users.ResetAccessFailedCountAsync(user);
        if (hadTwoFactor)
        {
            Check(await users.SetTwoFactorEnabledAsync(user, false));
            Check(await users.ResetAuthenticatorKeyAsync(user));
        }

        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PcServDbContext>>().CreateDbContextAsync();
        db.CurrentUserName = "server command";
        db.LogAudit("User.PasswordReset", "User", user.Id, $"Temporary password issued for {user.UserName} from the server.");
        await db.SaveChangesAsync();

        return new RecoveredAccount(user.UserName!, password, wasDisabled, hadTwoFactor);
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}

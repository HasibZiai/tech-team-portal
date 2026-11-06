using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Web.Services;

public sealed record UserRow(
    string Id,
    string UserName,
    string DisplayName,
    string? Role,
    bool IsDisabled,
    bool MustChangePassword,
    bool HasTwoFactor,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

/// <summary>Thrown for user-management actions that aren't allowed, with a message safe to show on screen.</summary>
public sealed class UserAdminException(string message) : Exception(message);

/// <summary>
/// Everything admins can do to accounts. Each user has exactly one role.
/// Guards against removing the last active admin, so nobody can lock everyone out.
/// </summary>
public sealed class UserAdminService(UserManager<ApplicationUser> userManager, AppDbFactory dbFactory)
{
    public async Task<List<UserRow>> ListAsync()
    {
        var users = await userManager.Users.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.UserName).ToListAsync();
        var rows = new List<UserRow>(users.Count);
        foreach (var u in users)
        {
            var roles = await userManager.GetRolesAsync(u);
            rows.Add(new UserRow(u.Id, u.UserName ?? "", u.DisplayName, HighestRole(roles), u.IsDisabled,
                u.MustChangePassword, u.TwoFactorEnabled, u.CreatedAt, u.LastLoginAt));
        }
        return rows;
    }

    /// <summary>Creates an account and returns its temporary password.</summary>
    public async Task<string> CreateAsync(string userName, string displayName, string role)
    {
        EnsureValidRole(role);
        userName = userName.Trim();

        if (await userManager.FindByNameAsync(userName) is not null)
        {
            throw new UserAdminException($"The username \"{userName}\" is already taken.");
        }

        var tempPassword = TempPasswordGenerator.Generate();
        var user = new ApplicationUser
        {
            UserName = userName,
            DisplayName = displayName.Trim(),
            MustChangePassword = true,
            LockoutEnabled = true,
        };

        ThrowIfFailed(await userManager.CreateAsync(user, tempPassword));
        ThrowIfFailed(await userManager.AddToRoleAsync(user, role));

        await AuditAsync("User.Created", user, $"{user.DisplayName} ({user.UserName}) created as {role}.");
        return tempPassword;
    }

    public async Task UpdateAsync(string userId, string displayName, string role, string actingUserId)
    {
        EnsureValidRole(role);
        var user = await FindAsync(userId);
        var roles = await userManager.GetRolesAsync(user);
        var oldRole = HighestRole(roles);

        if (oldRole == Roles.Admin && role != Roles.Admin)
        {
            if (userId == actingUserId) throw new UserAdminException("You can't remove your own Admin role. Ask another admin to do it.");
            await EnsureAnotherActiveAdminAsync(userId);
        }

        var changes = new List<string>();
        if (user.DisplayName != displayName.Trim())
        {
            changes.Add($"display name \"{user.DisplayName}\" → \"{displayName.Trim()}\"");
            user.DisplayName = displayName.Trim();
            ThrowIfFailed(await userManager.UpdateAsync(user));
        }

        if (oldRole != role || roles.Count != 1)
        {
            ThrowIfFailed(await userManager.RemoveFromRolesAsync(user, roles));
            ThrowIfFailed(await userManager.AddToRoleAsync(user, role));
            // Sign them out of other sessions so the new role takes effect right away.
            await userManager.UpdateSecurityStampAsync(user);
            changes.Add($"role {oldRole ?? "none"} → {role}");
        }

        if (changes.Count > 0)
        {
            await AuditAsync("User.Updated", user, $"{user.UserName}: {string.Join("; ", changes)}.");
        }
    }

    /// <summary>Replaces the user's password with a new temporary one and returns it.</summary>
    public async Task<string> ResetPasswordAsync(string userId)
    {
        var user = await FindAsync(userId);
        var tempPassword = TempPasswordGenerator.Generate();

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        ThrowIfFailed(await userManager.ResetPasswordAsync(user, token, tempPassword));

        user.MustChangePassword = true;
        ThrowIfFailed(await userManager.UpdateAsync(user));
        await userManager.SetLockoutEndDateAsync(user, user.IsDisabled ? DateTimeOffset.MaxValue : null);
        await userManager.ResetAccessFailedCountAsync(user);

        await AuditAsync("User.PasswordReset", user, $"Temporary password issued for {user.UserName}.");
        return tempPassword;
    }

    public async Task SetDisabledAsync(string userId, bool disabled, string actingUserId)
    {
        var user = await FindAsync(userId);
        if (user.IsDisabled == disabled) return;

        if (disabled)
        {
            if (userId == actingUserId) throw new UserAdminException("You can't disable your own account.");
            if (await userManager.IsInRoleAsync(user, Roles.Admin)) await EnsureAnotherActiveAdminAsync(userId);
        }

        user.IsDisabled = disabled;
        user.LockoutEnabled = true;
        user.LockoutEnd = disabled ? DateTimeOffset.MaxValue : null;
        ThrowIfFailed(await userManager.UpdateAsync(user));

        // Ends any active sessions (they're re-checked every few minutes).
        await userManager.UpdateSecurityStampAsync(user);

        await AuditAsync(disabled ? "User.Disabled" : "User.Enabled", user, $"{user.UserName} was {(disabled ? "disabled" : "re-enabled")}.");
    }

    private async Task EnsureAnotherActiveAdminAsync(string exceptUserId)
    {
        var admins = await userManager.GetUsersInRoleAsync(Roles.Admin);
        if (!admins.Any(a => a.Id != exceptUserId && !a.IsDisabled))
        {
            throw new UserAdminException("There must always be at least one active Admin. Make someone else an Admin first.");
        }
    }

    private async Task<ApplicationUser> FindAsync(string userId) =>
        await userManager.FindByIdAsync(userId) ?? throw new UserAdminException("That user no longer exists.");

    private async Task AuditAsync(string action, ApplicationUser user, string summary)
    {
        // Accounts are real, even for an Admin looking at the test version, so this belongs in the real log.
        await using var db = await dbFactory.CreateRealAsync();
        db.LogAudit(action, "User", user.Id, summary);
        await db.SaveChangesAsync();
    }

    private static string? HighestRole(IEnumerable<string> roles) => Roles.All.Reverse().FirstOrDefault(roles.Contains);

    private static void EnsureValidRole(string role)
    {
        if (!Roles.All.Contains(role)) throw new UserAdminException($"Unknown role \"{role}\".");
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded) throw new UserAdminException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}

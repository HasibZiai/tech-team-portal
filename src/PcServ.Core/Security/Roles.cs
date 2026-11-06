namespace PcServ.Core.Security;

/// <summary>
/// The four access levels in the portal. Each role includes everything the role before it can do.
/// </summary>
public static class Roles
{
    public const string Viewer = "Viewer";
    public const string Tech = "Tech";
    public const string Manager = "Manager";
    public const string Admin = "Admin";

    /// <summary>All roles, lowest access first.</summary>
    public static readonly IReadOnlyList<string> All = [Viewer, Tech, Manager, Admin];

    /// <summary>True if any of the roles held is the minimum role or higher. Unknown roles never pass.</summary>
    public static bool MeetsMinimum(IEnumerable<string> heldRoles, string minimumRole)
    {
        var required = IndexOf(minimumRole);
        return required >= 0 && heldRoles.Any(held => IndexOf(held) >= required);
    }

    private static int IndexOf(string role)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == role) return i;
        }
        return -1;
    }

    public static string Describe(string role) => role switch
    {
        Viewer => "Can view links, sites and maps.",
        Tech => "Viewer, plus can add and update day-to-day records.",
        Manager => "Tech, plus can edit base values (links, sites, statuses) and view the audit log.",
        Admin => "Everything, including managing users and app settings.",
        _ => "Unknown role."
    };
}

/// <summary>
/// Authorization policy names. Use these with [Authorize(Policy = ...)] or AuthorizeView.
/// </summary>
public static class Policies
{
    /// <summary>Any signed-in user with a role.</summary>
    public const string CanView = nameof(CanView);

    /// <summary>Tech, Manager or Admin.</summary>
    public const string CanEdit = nameof(CanEdit);

    /// <summary>Manager or Admin.</summary>
    public const string CanManage = nameof(CanManage);

    /// <summary>Admin only.</summary>
    public const string IsAdmin = nameof(IsAdmin);

    public static readonly IReadOnlyDictionary<string, string[]> RoleMap = new Dictionary<string, string[]>
    {
        [CanView] = [Roles.Viewer, Roles.Tech, Roles.Manager, Roles.Admin],
        [CanEdit] = [Roles.Tech, Roles.Manager, Roles.Admin],
        [CanManage] = [Roles.Manager, Roles.Admin],
        [IsAdmin] = [Roles.Admin],
    };
}

/// <summary>Custom claim types added to the signed-in user.</summary>
public static class AppClaimTypes
{
    public const string DisplayName = "pcserv:display_name";
    public const string MustChangePassword = "pcserv:must_change_password";
}

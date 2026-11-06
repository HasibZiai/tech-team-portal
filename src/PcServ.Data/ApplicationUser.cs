using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace PcServ.Data;

/// <summary>A local portal account. Admins create these; there is no public sign-up.</summary>
public class ApplicationUser : IdentityUser
{
    [MaxLength(120)]
    public string DisplayName { get; set; } = "";

    /// <summary>Set when an admin creates the account or resets the password.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Disabled accounts cannot sign in. Kept instead of deleting so history stays intact.</summary>
    public bool IsDisabled { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }
}

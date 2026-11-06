using System.ComponentModel.DataAnnotations;

namespace PcServ.Core.Entities;

/// <summary>A record of who changed what, written automatically on every save.</summary>
public class AuditEntry
{
    public long Id { get; set; }

    public DateTime Timestamp { get; set; }

    [MaxLength(256)]
    public string UserName { get; set; } = "";

    /// <summary>Created, Updated, Deleted, or a custom action like "User.PasswordReset".</summary>
    [MaxLength(60)]
    public string Action { get; set; } = "";

    [MaxLength(80)]
    public string EntityType { get; set; } = "";

    [MaxLength(64)]
    public string? EntityId { get; set; }

    /// <summary>Human-readable description of the change.</summary>
    [MaxLength(4000)]
    public string Summary { get; set; } = "";
}

/// <summary>A simple key/value setting that admins can change from the Settings page.</summary>
public class AppSetting
{
    [Key, MaxLength(100)]
    public string Key { get; set; } = "";

    [MaxLength(4000)]
    public string? Value { get; set; }
}

public static class SettingKeys
{
    public const string PersonalityCore = "PersonalityCore.Enabled";
    public const string OrganizationName = "Organization.Name";

    /// <summary>The Admins currently looking at the test version, separated by commas.</summary>
    public const string TestVersionUsers = "TestVersion.Users";
}

using System.ComponentModel.DataAnnotations;

namespace PcServ.Core.Entities;

/// <summary>
/// Someone who works at a site. Site techs keep this list for their own convenience, and it suggests names on
/// orders. Orders store the name itself, so removing someone here doesn't change past orders.
/// </summary>
public class SiteStaffMember : Entity
{
    public int SiteId { get; set; }
    public Site? Site { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    [MaxLength(120)]
    public string? JobTitle { get; set; }

    [MaxLength(60)]
    public string? Room { get; set; }

    [MaxLength(40)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    /// <summary>The person this site's orders usually go to. It fills in automatically on new orders. One per site.</summary>
    public bool IsOrderContact { get; set; }
}

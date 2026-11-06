using System.ComponentModel.DataAnnotations;

namespace PcServ.Core.Entities;

public enum RepairStatus
{
    InRepair,
    OnHoldForParts,
    Completed,
    BeyondRepair,
}

/// <summary>The wording for repair statuses, in one place so an organization can match its own system.</summary>
public static class RepairStatuses
{
    public static readonly IReadOnlyList<RepairStatus> All = [RepairStatus.InRepair, RepairStatus.OnHoldForParts, RepairStatus.Completed, RepairStatus.BeyondRepair];

    public static string Name(RepairStatus status) => status switch
    {
        RepairStatus.InRepair => "In for repairs",
        RepairStatus.OnHoldForParts => "On hold for parts",
        RepairStatus.Completed => "Completed",
        RepairStatus.BeyondRepair => "Can't be repaired",
        _ => status.ToString(),
    };

    /// <summary>What Inventory should say about a device at this status.</summary>
    public static string InventoryName(RepairStatus status) => status switch
    {
        RepairStatus.InRepair => "In for Repairs",
        RepairStatus.OnHoldForParts => "On Hold for Parts",
        RepairStatus.Completed => "Usable",
        RepairStatus.BeyondRepair => "Not Usable",
        _ => status.ToString(),
    };

    /// <summary>Whether the repair is over, so it has a date out and leaves the open list.</summary>
    public static bool IsDone(RepairStatus status) => status is RepairStatus.Completed or RepairStatus.BeyondRepair;
}

public enum RepairListKind
{
    Model,
    Reason,
}

/// <summary>
/// A choice in the Chromebook model or repair reason lists, which Managers keep. Retired rather than
/// deleted, so past repairs keep their wording.
/// </summary>
public class RepairListItem : Entity
{
    public RepairListKind Kind { get; set; }

    [Required, MaxLength(80)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }
}

/// <summary>A device the team has seen, so scanning its asset tag again fills in what's known.</summary>
public class Device : Entity
{
    [Required, MaxLength(40)]
    public string AssetTag { get; set; } = "";

    [MaxLength(60)]
    public string? SerialNumber { get; set; }

    public int? ModelId { get; set; }
    public RepairListItem? Model { get; set; }

    /// <summary>The site it was last seen at.</summary>
    public int? SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>When it was deprovisioned in the device console, after going out of circulation. Cleared if it comes back.</summary>
    public DateOnly? DeprovisionedOn { get; set; }

    [MaxLength(256)]
    public string? DeprovisionedBy { get; set; }

    public List<Repair> Repairs { get; set; } = [];
}

/// <summary>One trip through repairs. It holds no student details: fines and checkouts are kept in Inventory.</summary>
public class Repair : Entity
{
    public int DeviceId { get; set; }
    public Device? Device { get; set; }

    public int SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>The reason from the list. Null means <see cref="OtherReason"/> is used.</summary>
    public int? ReasonId { get; set; }
    public RepairListItem? Reason { get; set; }

    [MaxLength(200)]
    public string? OtherReason { get; set; }

    /// <summary>Where it was collected from, such as a cart, a classroom or the library.</summary>
    [MaxLength(80)]
    public string? PickupPoint { get; set; }

    public RepairStatus Status { get; set; } = RepairStatus.InRepair;

    public DateOnly DateIn { get; set; }
    public DateOnly? DateOut { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>
    /// The status Inventory was last updated to. When it differs from <see cref="Status"/>, Inventory is out of date,
    /// so changing the status flags it again without anyone having to remember.
    /// </summary>
    public RepairStatus? InventoryUpdatedTo { get; set; }
    public DateOnly? InventoryUpdatedOn { get; set; }

    [MaxLength(256)]
    public string? InventoryUpdatedBy { get; set; }

    /// <summary>Whether a fine should be assigned in Inventory. Who it's for stays in Inventory.</summary>
    public bool FineNeeded { get; set; }
    public bool FineDone { get; set; }
}

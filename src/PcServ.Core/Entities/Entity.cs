namespace PcServ.Core.Entities;

/// <summary>
/// Base class for every record the portal tracks. Created/updated stamps are filled in
/// automatically when saving, and changes are written to the audit log.
/// </summary>
public abstract class Entity
{
    public int Id { get; set; }

    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>A copy for edit forms, so cancelling a form leaves the original untouched.</summary>
    public T Copy<T>() where T : Entity => (T)MemberwiseClone();
}

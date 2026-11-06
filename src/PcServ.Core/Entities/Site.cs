using System.ComponentModel.DataAnnotations;

namespace PcServ.Core.Entities;

/// <summary>A school or district location.</summary>
public class Site : Entity
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    /// <summary>Short code techs use day to day, e.g. "LES" for Lincoln Elementary.</summary>
    [MaxLength(12)]
    public string? Code { get; set; }

    public SiteType Type { get; set; } = SiteType.Elementary;

    [MaxLength(250)]
    public string? Address { get; set; }

    [MaxLength(40)]
    public string? MainPhone { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public List<Building> Buildings { get; set; } = [];
}

public enum SiteType
{
    Elementary,
    Middle,
    High,
    Office,
    Warehouse,
    Other
}

/// <summary>A building (or wing/portable) at a site. Site maps attach here in a later phase.</summary>
public class Building : Entity
{
    public int SiteId { get; set; }
    public Site? Site { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    /// <summary>Number of floors; site maps will have one plan per floor.</summary>
    [Range(1, 20)]
    public int Floors { get; set; } = 1;

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public int SortOrder { get; set; }
}

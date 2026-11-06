using System.ComponentModel.DataAnnotations;

namespace PcServ.Core.Entities;

/// <summary>A group of links in the directory, e.g. "Vendors" or "Admin Consoles".</summary>
public class LinkCategory : Entity
{
    [Required, MaxLength(80)]
    public string Name { get; set; } = "";

    [MaxLength(250)]
    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public List<LinkItem> Links { get; set; } = [];
}

/// <summary>One entry in the links directory.</summary>
public class LinkItem : Entity
{
    public int CategoryId { get; set; }
    public LinkCategory? Category { get; set; }

    [Required, MaxLength(120)]
    public string Title { get; set; } = "";

    [Required, MaxLength(2000), Url]
    public string Url { get; set; } = "";

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Pinned links also show on the home page.</summary>
    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }
}

using System.ComponentModel.DataAnnotations;
using PcServ.Core.Security;

namespace PcServ.Core.Entities;

public class WikiSection : Entity
{
    [Required, MaxLength(80)]
    public string Name { get; set; } = "";

    public int SortOrder { get; set; }

    public List<WikiPage> Pages { get; set; } = [];
}

/// <summary>A wiki page. It holds what readers see. Earlier versions and people's unpublished work are kept separately.</summary>
public class WikiPage : Entity
{
    public int SectionId { get; set; }
    public WikiSection? Section { get; set; }

    [Required, MaxLength(150)]
    public string Title { get; set; } = "";

    public int SortOrder { get; set; }

    /// <summary>Who can read it. Either Viewer or Tech, because editors are Techs and must be able to reach what they edit.</summary>
    [Required, MaxLength(20)]
    public string MinimumRole { get; set; } = Roles.Viewer;

    /// <summary>Cleaned HTML. Empty until the page is first published.</summary>
    public string Html { get; set; } = "";

    /// <summary>The page's words without formatting, for searching.</summary>
    public string SearchText { get; set; } = "";

    /// <summary>The published version's number. 0 means it has never been published, so only editors see it.</summary>
    public int CurrentVersion { get; set; }

    public DateTime? PublishedAt { get; set; }

    [MaxLength(256)]
    public string? PublishedBy { get; set; }

    public List<WikiPageVersion> Versions { get; set; } = [];
}

/// <summary>One published version of a page. Every publish adds one, so nothing is ever overwritten.</summary>
public class WikiPageVersion : Entity
{
    public int PageId { get; set; }
    public WikiPage? Page { get; set; }

    public int Number { get; set; }

    [Required, MaxLength(150)]
    public string Title { get; set; } = "";

    public string Html { get; set; } = "";

    [MaxLength(200)]
    public string? Note { get; set; }
}

/// <summary>Someone's unpublished work on a page, saved as they type. Each person has their own.</summary>
public class WikiDraft : Entity
{
    public int PageId { get; set; }
    public WikiPage? Page { get; set; }

    [Required, MaxLength(256)]
    public string UserName { get; set; } = "";

    [MaxLength(150)]
    public string Title { get; set; } = "";

    public string Html { get; set; } = "";

    /// <summary>The version this was started from, to notice if someone else publishes in the meantime.</summary>
    public int BaseVersion { get; set; }
}

/// <summary>A picture in the wiki's image library. The random key is its address, so it can't be guessed.</summary>
public class WikiImage : Entity
{
    [Required, MaxLength(32)]
    public string StorageKey { get; set; } = "";

    [MaxLength(260)]
    public string FileName { get; set; } = "";

    public int Width { get; set; }
    public int Height { get; set; }
}

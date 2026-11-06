using System.ComponentModel.DataAnnotations;
using PcServ.Core.Security;

namespace PcServ.Core.Entities;

public enum SiteMapKind
{
    SitePlan,
    FloorPlan,
    Other
}

public enum MapLayerKind
{
    RoomLabels,
    Notes,
    Network,
    Custom,

    /// <summary>Pins for orders. Managed by the app, and always restricted, because pins name staff members.</summary>
    Orders
}

public enum MapFeatureType
{
    /// <summary>Text placed on the map, such as a room name.</summary>
    Label,

    /// <summary>A symbol placed on the map, such as an access point.</summary>
    Marker
}

public enum MapLinkStatus
{
    /// <summary>Found automatically or from clicked points. Shows nothing until a person confirms it.</summary>
    Suggested,
    Confirmed
}

/// <summary>Symbols a marker can use. Stored as text so new ones don't need a database change.</summary>
public static class MapSymbols
{
    public const string IndoorAccessPoint = "ap-indoor";
    public const string OutdoorAccessPoint = "ap-outdoor";
    public const string OutdoorAccessPoint577 = "ap-outdoor-577";
    public const string Mdf = "mdf";
    public const string Idf = "idf";
    public const string PhotoPin = "photo";
    public const string OrderPin = "order";

    /// <summary>The plain-language name for a symbol, as shown in legends.</summary>
    public static string DisplayName(string symbol) => symbol switch
    {
        IndoorAccessPoint => "Indoor access point",
        OutdoorAccessPoint => "Outdoor access point",
        OutdoorAccessPoint577 => "Outdoor access point (alternate)",
        Mdf => "MDF",
        Idf => "IDF",
        PhotoPin => "Photo or note",
        OrderPin => "Ordered item",
        _ => symbol,
    };
}

/// <summary>
/// One clean base drawing of a site or floor. Everything shown on top of it lives in
/// <see cref="MapLayer"/> records, so layers can be toggled, restricted and exported separately.
/// </summary>
public class SiteMap : Entity
{
    public int SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>Optional: the building this map covers. Empty for a whole-site plan.</summary>
    public int? BuildingId { get; set; }
    public Building? Building { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    public SiteMapKind Kind { get; set; } = SiteMapKind.SitePlan;

    [Range(1, 20)]
    public int? Floor { get; set; }

    /// <summary>Folder name for this map's files in the data folder. Never built from user input.</summary>
    [Required, MaxLength(32)]
    public string StorageKey { get; set; } = "";

    [MaxLength(260)]
    public string SourceFileName { get; set; } = "";

    /// <summary>The lowest role allowed to open this map at all, including its base image.</summary>
    [Required, MaxLength(20)]
    public string MinimumRole { get; set; } = Roles.Viewer;

    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    public int SortOrder { get; set; }

    public List<MapLayer> Layers { get; set; } = [];
}

/// <summary>
/// Shows one map's network layers on another map of the same site. X' = M00*X + M01*Y + M02 and
/// Y' = M10*X + M11*Y + M12, in 0-to-1 map coordinates. The layers are linked, not copied.
/// </summary>
public class MapLink : Entity
{
    public int SourceMapId { get; set; }
    public SiteMap? SourceMap { get; set; }

    public int TargetMapId { get; set; }
    public SiteMap? TargetMap { get; set; }

    public double M00 { get; set; }
    public double M01 { get; set; }
    public double M02 { get; set; }
    public double M10 { get; set; }
    public double M11 { get; set; }
    public double M12 { get; set; }

    /// <summary>How well the drawings matched when found automatically, from 0 to 1.</summary>
    public double Score { get; set; }

    public MapLinkStatus Status { get; set; } = MapLinkStatus.Suggested;
}

/// <summary>
/// Where an item sits on a linked map when a person has placed it by hand. It takes priority over the
/// link's calculated position, and affects only that map. This handles buildings drawn in different spots.
/// </summary>
public class MapFeaturePlacement : Entity
{
    public int MapLinkId { get; set; }
    public MapLink? MapLink { get; set; }

    public int MapFeatureId { get; set; }
    public MapFeature? MapFeature { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A set of items that can be switched on and off, visible only to certain roles.</summary>
public class MapLayer : Entity
{
    public int SiteMapId { get; set; }
    public SiteMap? SiteMap { get; set; }

    [Required, MaxLength(80)]
    public string Name { get; set; } = "";

    public MapLayerKind Kind { get; set; } = MapLayerKind.Custom;

    /// <summary>The lowest role allowed to see this layer. The server never sends it to anyone below that.</summary>
    [Required, MaxLength(20)]
    public string MinimumRole { get; set; } = Roles.Viewer;

    [MaxLength(9)]
    public string Color { get; set; } = "#1F6FC5";

    public bool VisibleByDefault { get; set; } = true;

    public int SortOrder { get; set; }

    public List<MapFeature> Features { get; set; } = [];

    /// <summary>The kinds of item on a layer people made themselves. Imported layers use fixed symbols instead.</summary>
    public List<MapLegendEntry> LegendEntries { get; set; } = [];
}

/// <summary>One kind of item on a custom layer: a name, with the shape and color that stand for it on the map.</summary>
public class MapLegendEntry : Entity
{
    public int MapLayerId { get; set; }
    public MapLayer? MapLayer { get; set; }

    [Required, MaxLength(60)]
    public string Name { get; set; } = "";

    [Required, MaxLength(20)]
    public string Shape { get; set; } = MapShapes.Circle;

    [Required, MaxLength(9)]
    public string Color { get; set; } = "#12315C";

    public int SortOrder { get; set; }
}

/// <summary>Shapes for custom legends. They differ in outline, so a legend doesn't rely on color alone.</summary>
public static class MapShapes
{
    public const string Circle = "circle";
    public const string Square = "square";
    public const string Triangle = "triangle";
    public const string Diamond = "diamond";
    public const string Star = "star";

    public static readonly IReadOnlyList<string> All = [Circle, Square, Triangle, Diamond, Star];

    public static string DisplayName(string shape) => shape switch
    {
        Circle => "Circle",
        Square => "Square",
        Triangle => "Triangle",
        Diamond => "Diamond",
        Star => "Star",
        _ => "Unknown",
    };

    /// <summary>The outline as SVG path data, on a 24 by 24 grid. Keep in step with CUSTOM_SHAPES in sitemap.js.</summary>
    public static string Path(string shape) => shape switch
    {
        Square => "M3 3h18v18H3z",
        Triangle => "M12 3l10 18H2z",
        Diamond => "M12 2l10 10-10 10L2 12z",
        Star => "M12 2l3 6.6 7.2.8-5.3 4.8 1.5 7.1L12 17.7 5.6 21.3l1.5-7.1L1.8 9.4 9 8.6z",
        _ => "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20z",
    };
}

/// <summary>The colors custom legends can use. All are dark enough to stand out on a white plan, and none are orange.</summary>
public static class MapColors
{
    public static readonly IReadOnlyList<(string Hex, string Name)> All =
    [
        ("#12315C", "Navy"),
        ("#1F6FC5", "Blue"),
        ("#0B7A75", "Teal"),
        ("#2E7D32", "Green"),
        ("#6A3FA0", "Purple"),
        ("#B3261E", "Red"),
        ("#1B2735", "Black"),
    ];
}

/// <summary>
/// One item on a layer. X and Y run from 0 to 1 across the map, measured from the top-left corner,
/// so positions stay correct however large the base image is.
/// </summary>
public class MapFeature : Entity
{
    public int MapLayerId { get; set; }
    public MapLayer? MapLayer { get; set; }

    public MapFeatureType Type { get; set; } = MapFeatureType.Label;

    public double X { get; set; }
    public double Y { get; set; }

    [MaxLength(200)]
    public string? Label { get; set; }

    [MaxLength(40)]
    public string? Symbol { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Set when an import wasn't sure about this item, until a person has checked it.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>Notes people write about this item. (Notes above holds why an import flagged it.)</summary>
    [MaxLength(2000)]
    public string? Description { get; set; }

    public List<MapPhoto> Photos { get; set; } = [];

    /// <summary>What this item is, on a custom layer. Imported items use Symbol instead.</summary>
    public int? LegendEntryId { get; set; }
    public MapLegendEntry? LegendEntry { get; set; }

    /// <summary>The order this is a pin for, if any.</summary>
    public Order? Order { get; set; }
}

/// <summary>A picture attached to a map item. Files are in the map's folder under a generated name.</summary>
public class MapPhoto : Entity
{
    public int MapFeatureId { get; set; }
    public MapFeature? MapFeature { get; set; }

    /// <summary>The generated file name. Never built from anything a person typed or uploaded.</summary>
    [Required, MaxLength(32)]
    public string StorageKey { get; set; } = "";

    /// <summary>The uploaded file's name, for display only.</summary>
    [MaxLength(260)]
    public string FileName { get; set; } = "";

    [MaxLength(300)]
    public string? Caption { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
}

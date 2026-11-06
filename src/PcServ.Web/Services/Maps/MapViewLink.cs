using System.Globalization;

namespace PcServ.Web.Services.Maps;

/// <summary>Part of a map, as shares of its size from the top-left corner.</summary>
public sealed record MapArea(double Left, double Top, double Right, double Bottom)
{
    public static readonly MapArea Whole = new(0, 0, 1, 1);

    /// <summary>Smaller areas than this aren't a real view of a map.</summary>
    private const double MinimumSpan = .001;

    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool IsWhole => Left <= 0 && Top <= 0 && Right >= 1 && Bottom >= 1;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>Trims an area to the map, or returns null if it isn't usable.</summary>
    public static MapArea? Create(double left, double top, double right, double bottom)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom)) return null;
        var area = new MapArea(Math.Clamp(left, 0, 1), Math.Clamp(top, 0, 1), Math.Clamp(right, 0, 1), Math.Clamp(bottom, 0, 1));
        return area.Width >= MinimumSpan && area.Height >= MinimumSpan ? area : null;
    }
}

/// <summary>
/// Links that reopen a map with the same layers and the same part of it showing. A link holds no
/// permission: whoever opens it signs in as usual, and sees only what their own role allows.
/// </summary>
public static class MapViewLink
{
    private const string NoLayers = "none";
    private const int MaxLayers = 200;

    public static string Build(int mapId, IEnumerable<int> layerIds, MapArea area)
    {
        var ids = string.Join(',', layerIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var link = $"maps/{mapId}?layers={(ids.Length == 0 ? NoLayers : ids)}";
        return area.IsWhole ? link : link + "&view=" + string.Join(',', new[] { area.Left, area.Top, area.Right, area.Bottom }.Select(Number));
    }

    /// <summary>The layers a link asks for, or null if it doesn't say. Anything that isn't a layer number is ignored.</summary>
    public static IReadOnlySet<int>? ParseLayers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim() == NoLayers) return new HashSet<int>();
        var ids = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Take(MaxLayers)
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
            .OfType<int>()
            .ToHashSet();

        // A damaged link shouldn't switch every layer off.
        return ids.Count == 0 ? null : ids;
    }

    /// <summary>The area a link asks for, or null if it doesn't say or makes no sense.</summary>
    public static MapArea? ParseArea(string? value)
    {
        var parts = value?.Split(',') ?? [];
        if (parts.Length != 4) return null;
        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) return null;
        }
        return MapArea.Create(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

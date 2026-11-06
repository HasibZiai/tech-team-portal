using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Web.Services.Maps;

public sealed record MapLegendEntryEdit(int? Id, string Name, string Shape, string Color);

/// <summary>
/// Layers that people make themselves, each with its own legend. They're shared: everyone at or above the
/// layer's role can see them. Techs and above can change them. Imported layers can't be changed here.
/// </summary>
public sealed class MapLayerService(AppDbFactory dbFactory, MapFileStore files)
{
    public const int MaxLegendEntries = 20;

    /// <summary>Layer names the app itself uses.</summary>
    private static readonly string[] ReservedNames = [MapPhotoService.PinLayerName, OrderService.PinLayerName, SiteMapService.RoomLabelsLayerName, SiteMapService.NotesLayerName];

    /// <summary>Whether this is a layer people can change on this map. A layer linked from another map is changed on that map.</summary>
    public static bool CanChange(MapLayerView layer, int mapId) =>
        layer.MapId == mapId && layer.Kind == nameof(MapLayerKind.Custom) && layer.Name != MapPhotoService.PinLayerName;

    /// <summary>Starts visible to Techs and above, so nothing half-finished reaches everyone. Managers can open it up.</summary>
    public async Task<MapLayerView> CreateLayerAsync(int mapId, string name, IReadOnlyList<MapLegendEntryEdit> legend, ClaimsPrincipal user)
    {
        RequireEdit(user);
        await using var db = await dbFactory.CreateAsync();
        var map = await db.SiteMaps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mapId) ?? throw new InvalidOperationException("That map no longer exists.");
        if (!Roles.MeetsMinimum(HeldRoles(user), map.MinimumRole)) throw new InvalidOperationException("You don't have permission to change this map.");

        var cleanName = await CheckNameAsync(db, mapId, name, null);
        CheckLegend(legend);

        var order = await db.MapLayers.Where(l => l.SiteMapId == mapId).Select(l => (int?)l.SortOrder).MaxAsync() ?? -1;
        var layer = new MapLayer
        {
            SiteMapId = mapId,
            Name = cleanName,
            Kind = MapLayerKind.Custom,
            MinimumRole = Roles.Tech,
            Color = legend[0].Color,
            SortOrder = order + 1,
            LegendEntries = legend.Select((e, i) => new MapLegendEntry { Name = e.Name.Trim(), Shape = e.Shape, Color = e.Color, SortOrder = i }).ToList(),
        };
        db.MapLayers.Add(layer);
        await db.SaveChangesAsync();
        return ToView(layer);
    }

    /// <summary>Renames a layer and updates its legend. A legend entry still used by items can't be removed.</summary>
    public async Task<MapLayerView> UpdateLayerAsync(int layerId, string name, IReadOnlyList<MapLegendEntryEdit> legend, ClaimsPrincipal user)
    {
        RequireEdit(user);
        await using var db = await dbFactory.CreateAsync();
        var layer = await LoadChangeableAsync(db, layerId, user);
        var cleanName = await CheckNameAsync(db, layer.SiteMapId, name, layerId);
        CheckLegend(legend);

        var keptIds = legend.Select(e => e.Id).OfType<int>().ToHashSet();
        var removed = layer.LegendEntries.Where(e => !keptIds.Contains(e.Id)).ToList();
        var inUse = removed.FirstOrDefault(e => layer.Features.Any(f => f.LegendEntryId == e.Id));
        if (inUse is not null)
        {
            throw new InvalidOperationException($"\"{inUse.Name}\" is still used by items on this layer. Delete those items first.");
        }
        db.MapLegendEntries.RemoveRange(removed);

        for (var i = 0; i < legend.Count; i++)
        {
            var edit = legend[i];
            var entry = edit.Id is null ? null : layer.LegendEntries.FirstOrDefault(e => e.Id == edit.Id);
            if (entry is null)
            {
                entry = new MapLegendEntry();
                layer.LegendEntries.Add(entry);
            }
            (entry.Name, entry.Shape, entry.Color, entry.SortOrder) = (edit.Name.Trim(), edit.Shape, edit.Color, i);
        }

        layer.Name = cleanName;
        layer.Color = legend[0].Color;
        await db.SaveChangesAsync();
        return ToView(layer);
    }

    /// <summary>Deletes a layer, its items, and those items' photos.</summary>
    public async Task DeleteLayerAsync(int layerId, ClaimsPrincipal user)
    {
        RequireEdit(user);
        await using var db = await dbFactory.CreateAsync();
        var layer = await LoadChangeableAsync(db, layerId, user);
        var mapKey = await db.SiteMaps.Where(m => m.Id == layer.SiteMapId).Select(m => m.StorageKey).FirstAsync();
        var photoKeys = await db.MapPhotos.Where(p => p.MapFeature!.MapLayerId == layerId).Select(p => p.StorageKey).ToListAsync();

        db.MapLayers.Remove(layer);
        await db.SaveChangesAsync();
        foreach (var key in photoKeys) files.DeletePhoto(mapKey, key);
    }

    /// <summary>Adds an item using one of a layer's legend entries. The position is on the layer's own map.</summary>
    public async Task<(MapLayerView Layer, MapFeatureView Feature)> AddItemAsync(int layerId, int entryId, double x, double y, ClaimsPrincipal user)
    {
        RequireEdit(user);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("That isn't a position on the map.");
        await using var db = await dbFactory.CreateAsync();
        var layer = await LoadChangeableAsync(db, layerId, user);
        var entry = layer.LegendEntries.FirstOrDefault(e => e.Id == entryId) ?? throw new InvalidOperationException("That legend entry no longer exists.");

        var feature = new MapFeature
        {
            MapLayerId = layerId,
            Type = MapFeatureType.Marker,
            X = Math.Clamp(x, 0, 1),
            Y = Math.Clamp(y, 0, 1),
            Label = entry.Name,
            LegendEntryId = entry.Id,
        };
        db.MapFeatures.Add(feature);
        await db.SaveChangesAsync();
        return (ToView(layer), new MapFeatureView(feature.Id, feature.Type.ToString(), feature.X, feature.Y, feature.Label, null, null, false, LegendEntryId: entry.Id));
    }

    private static async Task<MapLayer> LoadChangeableAsync(PcServDbContext db, int layerId, ClaimsPrincipal user)
    {
        var layer = await db.MapLayers.Include(l => l.SiteMap).Include(l => l.LegendEntries).Include(l => l.Features).FirstOrDefaultAsync(l => l.Id == layerId)
            ?? throw new InvalidOperationException("That layer no longer exists.");
        var held = HeldRoles(user);
        if (!Roles.MeetsMinimum(held, layer.MinimumRole) || !Roles.MeetsMinimum(held, layer.SiteMap!.MinimumRole))
        {
            throw new InvalidOperationException("You don't have permission to change that layer.");
        }
        if (layer.Kind != MapLayerKind.Custom || layer.Name == MapPhotoService.PinLayerName)
        {
            throw new InvalidOperationException("This layer is managed by the app, so it can't be changed here.");
        }
        return layer;
    }

    private static async Task<string> CheckNameAsync(PcServDbContext db, int mapId, string name, int? layerId)
    {
        var clean = name?.Trim() ?? "";
        if (clean.Length == 0) throw new InvalidOperationException("Give the layer a name.");
        if (clean.Length > 80) throw new InvalidOperationException("Layer names can be up to 80 characters.");
        if (ReservedNames.Contains(clean, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"\"{clean}\" is used by the app. Choose another name.");

        var others = await db.MapLayers.Where(l => l.SiteMapId == mapId && l.Id != layerId).Select(l => l.Name).ToListAsync();
        if (others.Contains(clean, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"This map already has a layer called \"{clean}\".");
        return clean;
    }

    private static void CheckLegend(IReadOnlyList<MapLegendEntryEdit> legend)
    {
        if (legend.Count == 0) throw new InvalidOperationException("A layer needs at least one legend entry.");
        if (legend.Count > MaxLegendEntries) throw new InvalidOperationException($"A layer can have up to {MaxLegendEntries} legend entries.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in legend)
        {
            var name = entry.Name?.Trim() ?? "";
            if (name.Length == 0) throw new InvalidOperationException("Every legend entry needs a name.");
            if (name.Length > 60) throw new InvalidOperationException("Legend names can be up to 60 characters.");
            if (!names.Add(name)) throw new InvalidOperationException($"The legend has \"{name}\" twice.");
            if (!MapShapes.All.Contains(entry.Shape)) throw new InvalidOperationException("That isn't one of the available shapes.");
            if (MapColors.All.All(c => c.Hex != entry.Color)) throw new InvalidOperationException("That isn't one of the available colors.");
        }
    }

    private static MapLayerView ToView(MapLayer layer) => new(layer.Id, layer.Name, layer.Kind.ToString(), layer.MinimumRole, layer.Color, layer.VisibleByDefault, [],
        layer.LegendEntries.OrderBy(e => e.SortOrder).Select(e => new MapLegendEntryView(e.Id, e.Name, e.Shape, e.Color)).ToList(), layer.SiteMapId);

    private static void RequireEdit(ClaimsPrincipal user)
    {
        if (!Roles.MeetsMinimum(HeldRoles(user), Roles.Tech)) throw new InvalidOperationException("You don't have permission to change layers.");
    }

    private static List<string> HeldRoles(ClaimsPrincipal user) => Roles.All.Where(user.IsInRole).ToList();
}

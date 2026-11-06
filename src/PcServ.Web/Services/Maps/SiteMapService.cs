using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Web.Services.Maps;

public sealed record MapFeatureView(int Id, string Type, double X, double Y, string? Label, string? Symbol, string? Notes, bool NeedsReview, string? Description = null, int PhotoCount = 0, int? LegendEntryId = null, int? OrderId = null);

public sealed record MapLegendEntryView(int Id, string Name, string Shape, string Color);

public sealed record MapLayerView(int Id, string Name, string Kind, string MinimumRole, string Color, bool VisibleByDefault, IReadOnlyList<MapFeatureView> Features, IReadOnlyList<MapLegendEntryView>? Legend = null, int MapId = 0);

public sealed record MapView(int Id, string Name, string SiteName, string? BuildingName, SiteMapKind Kind, int? Floor, string MinimumRole, int ImageWidth, int ImageHeight, IReadOnlyList<MapLayerView> Layers);

public sealed record MapSummary(int Id, string Name, int SiteId, string SiteName, string? BuildingName, SiteMapKind Kind, int? Floor, int LayerCount);

public sealed record MapUpload(int SiteId, int? BuildingId, string Name, SiteMapKind Kind, int? Floor, string FileName, byte[] File, string MinimumRole = Roles.Viewer);

public sealed record MapFeatureEdit(string? Name, string? Description, bool Delete);

public sealed record MapLayerEdit(string? Name, IReadOnlyList<MapLegendEntryEdit> Legend, bool Delete);

public sealed record MapUploadResult(int MapId, int LabelCount, int NoteCount, int SkippedAnnotations, int PageCount);

/// <summary>
/// Everything to do with site maps. Access is enforced here, on the server: a map or layer above
/// the user's role is never loaded for them, so it can't reach their browser.
/// </summary>
public sealed class SiteMapService(AppDbFactory dbFactory, TestVersion testVersion, MapFileStore files)
{
    public const string RoomLabelsLayerName = "Room labels";
    public const string NotesLayerName = "Title and notes";

    private static readonly HashSet<string> PdfExtensions = [".pdf"];
    private static readonly HashSet<string> ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>Largest file accepted. The biggest real map tested was about 13 MB.</summary>
    public const long MaxFileBytes = 50 * 1024 * 1024;

    public async Task<List<MapSummary>> ListAsync(ClaimsPrincipal user)
    {
        var held = HeldRoles(user);
        await using var db = await dbFactory.CreateAsync();

        var maps = await db.SiteMaps.AsNoTracking()
            .Include(m => m.Site)
            .Include(m => m.Building)
            .Include(m => m.Layers)
            .OrderBy(m => m.Site!.SortOrder).ThenBy(m => m.Site!.Name)
            .ThenBy(m => m.SortOrder).ThenBy(m => m.Name)
            .ToListAsync();

        return maps.Where(m => Roles.MeetsMinimum(held, m.MinimumRole))
            .Select(m => new MapSummary(m.Id, m.Name, m.SiteId, m.Site!.Name, m.Building?.Name, m.Kind, m.Floor,
                m.Layers.Count(l => Roles.MeetsMinimum(held, l.MinimumRole))))
            .ToList();
    }

    /// <summary>Loads a map with only the layers this user may see, or null if it doesn't exist or they can't open it.</summary>
    public async Task<MapView?> GetAsync(int mapId, ClaimsPrincipal user)
    {
        var held = HeldRoles(user);
        await using var db = await dbFactory.CreateAsync();

        var map = await db.SiteMaps.AsNoTracking()
            .Include(m => m.Site)
            .Include(m => m.Building)
            .FirstOrDefaultAsync(m => m.Id == mapId);
        if (map is null || !Roles.MeetsMinimum(held, map.MinimumRole)) return null;

        // Filter before touching features, so restricted items are never even read.
        var allowedRoles = Roles.All.Where(r => Roles.MeetsMinimum(held, r)).ToList();
        var layers = await db.MapLayers.AsNoTracking()
            .Where(l => l.SiteMapId == mapId && allowedRoles.Contains(l.MinimumRole))
            .Include(l => l.LegendEntries).Include(l => l.Features).ThenInclude(f => f.Photos).Include(l => l.Features).ThenInclude(f => f.Order)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .ToListAsync();

        var views = layers.Select(l => new MapLayerView(l.Id, l.Name, l.Kind.ToString(), l.MinimumRole, l.Color, l.VisibleByDefault,
            l.Features.Where(IsShown).Select(f => ViewOf(f, f.X, f.Y)).ToList(), LegendOf(l), l.SiteMapId)).ToList();

        // Layers linked from another map, through a confirmed alignment. The other map's own access rule still
        // applies, so a Viewer can't reach a restricted map's layers by opening a plan instead.
        var links = await db.MapLinks.AsNoTracking()
            .Where(l => l.TargetMapId == mapId && l.Status == MapLinkStatus.Confirmed && allowedRoles.Contains(l.SourceMap!.MinimumRole))
            .ToListAsync();
        foreach (var link in links)
        {
            var linked = await db.MapLayers.AsNoTracking()
                .Where(l => l.SiteMapId == link.SourceMapId && allowedRoles.Contains(l.MinimumRole))
                .Include(l => l.LegendEntries).Include(l => l.Features).ThenInclude(f => f.Photos).Include(l => l.Features).ThenInclude(f => f.Order)
                .OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
                .ToListAsync();
            var transform = new MapTransform(link.M00, link.M01, link.M02, link.M10, link.M11, link.M12);
            var placements = await db.MapFeaturePlacements.AsNoTracking()
                .Where(p => p.MapLinkId == link.Id)
                .ToDictionaryAsync(p => p.MapFeatureId, p => (p.X, p.Y));
            views.AddRange(linked.Select(l => new MapLayerView(l.Id, l.Name, l.Kind.ToString(), l.MinimumRole, l.Color, l.VisibleByDefault,
                l.Features.Where(IsShown).Select(f => (Feature: f, Position: placements.TryGetValue(f.Id, out var placed) ? placed : transform.Apply(f.X, f.Y)))
                    .Where(p => p.Position.X is >= 0 and <= 1 && p.Position.Y is >= 0 and <= 1)
                    .Select(p => ViewOf(p.Feature, p.Position.X, p.Position.Y))
                    .ToList(), LegendOf(l), l.SiteMapId)));
        }

        return new MapView(map.Id, map.Name, map.Site!.Name, map.Building?.Name, map.Kind, map.Floor, map.MinimumRole, map.ImageWidth, map.ImageHeight, views);
    }

    /// <summary>A finished order's pin stays stored, in case the order is reopened, but no longer shows.</summary>
    private static bool IsShown(MapFeature feature) => feature.Order is null || OrderStages.IsOpen(feature.Order.Status);

    private static MapFeatureView ViewOf(MapFeature f, double x, double y) => new(f.Id, f.Type.ToString(), x, y,
        f.Order is null ? f.Label : OrderService.PinLabel(f.Order), f.Symbol, f.Notes, f.NeedsReview, f.Description, f.Photos.Count, f.LegendEntryId, f.Order?.Id);

    private static List<MapLegendEntryView> LegendOf(MapLayer layer) =>
        layer.LegendEntries.OrderBy(e => e.SortOrder).Select(e => new MapLegendEntryView(e.Id, e.Name, e.Shape, e.Color)).ToList();

    /// <summary>
    /// The base image for a map, or null if it doesn't exist or this user can't open it. For the
    /// image endpoint: web endpoints don't have the Blazor sign-in context AppDbFactory uses,
    /// and this read has nothing to audit.
    /// </summary>
    public async Task<string?> BaseImagePathAsync(int mapId, ClaimsPrincipal user)
    {
        await using var db = await testVersion.CreateForAsync(user);
        var map = await db.SiteMaps.AsNoTracking().Where(m => m.Id == mapId)
            .Select(m => new { m.StorageKey, m.MinimumRole }).FirstOrDefaultAsync();
        if (map is null || !Roles.MeetsMinimum(HeldRoles(user), map.MinimumRole)) return null;
        return files.BaseImagePath(map.StorageKey);
    }

    /// <summary>
    /// Adds a map from an uploaded PDF or image. In a PDF with text boxes, those become layers
    /// and are left out of the base image, so they can be switched off.
    /// </summary>
    public async Task<MapUploadResult> CreateFromUploadAsync(MapUpload upload)
    {
        if (upload.File.Length == 0) throw new MapFileException("That file is empty.");
        if (upload.File.Length > MaxFileBytes) throw new MapFileException("That file is too large. The limit is 50 MB.");

        var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
        RenderedImage image;
        MapPdfContents text;

        if (PdfExtensions.Contains(extension))
        {
            text = MapPdfReader.ReadText(upload.File);
            // Text boxes that were imported come out of the image. On a map with none to
            // import, keep annotations in case they're drawings someone added.
            var imported = text.Labels.Count + text.Notes.Count > 0;
            image = MapPdfReader.RenderPdf(upload.File, includeAnnotations: !imported);
        }
        else if (ImageExtensions.Contains(extension))
        {
            text = new MapPdfContents([], [], 1, 0);
            image = MapPdfReader.RenderImage(upload.File);
        }
        else
        {
            throw new MapFileException("Please choose a PDF, PNG or JPEG file.");
        }

        return await SaveMapAsync(upload, image, text);
    }

    /// <summary>Saves a finished map with its files, layers and one audit entry.</summary>
    public async Task<MapUploadResult> SaveMapAsync(MapUpload upload, RenderedImage image, MapPdfContents text, IEnumerable<MapLayer>? extraLayers = null)
    {
        var storageKey = MapFileStore.NewStorageKey();
        await files.SaveAsync(storageKey, upload.FileName, upload.File, image.Png);

        try
        {
            await using var db = await dbFactory.CreateAsync();
            db.SuppressEntityAudit = true;

            var map = new SiteMap
            {
                SiteId = upload.SiteId,
                BuildingId = upload.BuildingId,
                Name = upload.Name.Trim(),
                Kind = upload.Kind,
                Floor = upload.Floor,
                MinimumRole = upload.MinimumRole,
                StorageKey = storageKey,
                SourceFileName = FileNames.WithoutFolders(upload.FileName),
                ImageWidth = image.Width,
                ImageHeight = image.Height,
            };

            if (text.Labels.Count > 0)
            {
                map.Layers.Add(TextLayer(RoomLabelsLayerName, MapLayerKind.RoomLabels, "#12315C", 0, text.Labels));
            }
            if (text.Notes.Count > 0)
            {
                map.Layers.Add(TextLayer(NotesLayerName, MapLayerKind.Notes, "#37474F", 1, text.Notes));
            }
            if (extraLayers is not null) map.Layers.AddRange(extraLayers);

            db.SiteMaps.Add(map);
            await db.SaveChangesAsync();

            var featureCount = map.Layers.Sum(l => l.Features.Count);
            db.LogAudit("Map.Added", nameof(SiteMap), map.Id.ToString(),
                $"{map.Name}: {map.Layers.Count} layer(s), {featureCount} item(s), from {map.SourceFileName}.");
            await db.SaveChangesAsync();

            return new MapUploadResult(map.Id, text.Labels.Count, text.Notes.Count, text.SkippedAnnotations, text.PageCount);
        }
        catch
        {
            // Don't leave sensitive files behind for a map that didn't save.
            files.Delete(storageKey);
            throw;
        }
    }

    public async Task SetLayerRoleAsync(int layerId, string minimumRole)
    {
        if (!Roles.All.Contains(minimumRole)) throw new ArgumentException($"Unknown role \"{minimumRole}\".", nameof(minimumRole));

        await using var db = await dbFactory.CreateAsync();
        var layer = await db.MapLayers.FindAsync(layerId) ?? throw new InvalidOperationException("That layer no longer exists.");
        if (layer.Kind == MapLayerKind.Orders) throw new InvalidOperationException("Order pins name staff members, so they stay limited to Techs and above.");
        layer.MinimumRole = minimumRole;
        await db.SaveChangesAsync();
    }

    public async Task SetMapRoleAsync(int mapId, string minimumRole)
    {
        if (!Roles.All.Contains(minimumRole)) throw new ArgumentException($"Unknown role \"{minimumRole}\".", nameof(minimumRole));

        await using var db = await dbFactory.CreateAsync();
        var map = await db.SiteMaps.FindAsync(mapId) ?? throw new InvalidOperationException("That map no longer exists.");
        map.MinimumRole = minimumRole;
        await db.SaveChangesAsync();
    }

    /// <summary>Saves a corrected item name and marks the item as checked.</summary>
    public async Task UpdateFeatureAsync(int featureId, string? label)
    {
        await using var db = await dbFactory.CreateAsync();
        var feature = await db.MapFeatures.FindAsync(featureId) ?? throw new InvalidOperationException("That item no longer exists.");
        feature.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        feature.NeedsReview = false;
        await db.SaveChangesAsync();
    }

    /// <summary>Saves an item's name and written notes, and marks it as checked.</summary>
    public async Task UpdateFeatureAsync(int featureId, string? label, string? description)
    {
        await using var db = await dbFactory.CreateAsync();
        var feature = await db.MapFeatures.FindAsync(featureId) ?? throw new InvalidOperationException("That item no longer exists.");
        feature.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        feature.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        feature.NeedsReview = false;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Moves an item as seen on one map. On its own map, the item itself moves, and maps linked to it follow.
    /// On a linked map, the position is saved for that map only, because the two drawings can legitimately differ.
    /// Returns the item's notes, which change if moving it settled the reason it was flagged.
    /// </summary>
    public async Task<string?> MoveFeatureAsync(int mapId, int featureId, double x, double y, ClaimsPrincipal user)
    {
        var held = HeldRoles(user);
        if (!Roles.MeetsMinimum(held, Roles.Tech)) throw new InvalidOperationException("You don't have permission to move items.");
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("That isn't a valid position.");
        (x, y) = (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));

        await using var db = await dbFactory.CreateAsync();
        var feature = await db.MapFeatures.Include(f => f.MapLayer).ThenInclude(l => l!.SiteMap).FirstOrDefaultAsync(f => f.Id == featureId)
            ?? throw new InvalidOperationException("That item no longer exists.");
        var targetMap = await db.SiteMaps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mapId)
            ?? throw new InvalidOperationException("That map no longer exists.");

        if (!Roles.MeetsMinimum(held, feature.MapLayer!.MinimumRole) || !Roles.MeetsMinimum(held, feature.MapLayer.SiteMap!.MinimumRole) || !Roles.MeetsMinimum(held, targetMap.MinimumRole))
        {
            throw new InvalidOperationException("You don't have permission to move that item.");
        }

        if (feature.MapLayer.SiteMapId == mapId)
        {
            feature.X = x;
            feature.Y = y;
        }
        else
        {
            var link = await db.MapLinks.FirstOrDefaultAsync(l => l.SourceMapId == feature.MapLayer.SiteMapId && l.TargetMapId == mapId && l.Status == MapLinkStatus.Confirmed)
                ?? throw new InvalidOperationException("That item isn't on this map.");
            var placement = await db.MapFeaturePlacements.FirstOrDefaultAsync(p => p.MapLinkId == link.Id && p.MapFeatureId == featureId);
            if (placement is null)
            {
                db.MapFeaturePlacements.Add(new MapFeaturePlacement { MapLinkId = link.Id, MapFeatureId = featureId, X = x, Y = y });
            }
            else
            {
                placement.X = x;
                placement.Y = y;
            }
        }

        // A marker imported onto its name was flagged for its position. Now it's been placed, only the name needs checking.
        if (feature.Notes == NetworkLayerImportService.MissingSymbolNote) feature.Notes = NetworkLayerImportService.CheckNameNote;

        await db.SaveChangesAsync();
        return feature.Notes;
    }

    public async Task DeleteFeatureAsync(int featureId)
    {
        await using var db = await dbFactory.CreateAsync();
        var feature = await db.MapFeatures.Include(f => f.Photos).Include(f => f.MapLayer).ThenInclude(l => l!.SiteMap).FirstOrDefaultAsync(f => f.Id == featureId);
        if (feature is null) return;
        var mapKey = feature.MapLayer!.SiteMap!.StorageKey;
        var photoKeys = feature.Photos.Select(p => p.StorageKey).ToList();
        db.MapFeatures.Remove(feature);
        await db.SaveChangesAsync();

        // Don't leave pictures behind for an item that's gone.
        foreach (var key in photoKeys) files.DeletePhoto(mapKey, key);
    }

    /// <summary>Deletes a map, its layers and its files.</summary>
    public async Task DeleteAsync(int mapId)
    {
        await using var db = await dbFactory.CreateAsync();
        var map = await db.SiteMaps.FindAsync(mapId);
        if (map is null) return;

        db.SuppressEntityAudit = true;
        db.SiteMaps.Remove(map);
        db.LogAudit("Map.Deleted", nameof(SiteMap), map.Id.ToString(), $"{map.Name} and its layers were deleted.");
        await db.SaveChangesAsync();

        files.Delete(map.StorageKey);
    }

    public async Task<int> CountForSiteAsync(int siteId)
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.SiteMaps.CountAsync(m => m.SiteId == siteId);
    }

    private static MapLayer TextLayer(string name, MapLayerKind kind, string color, int order, IEnumerable<ImportedText> items) => new()
    {
        Name = name,
        Kind = kind,
        MinimumRole = Roles.Viewer,
        Color = color,
        SortOrder = order,
        Features = items.Select(i => new MapFeature { Type = MapFeatureType.Label, X = i.X, Y = i.Y, Label = Truncate(i.Text, 200) }).ToList(),
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static List<string> HeldRoles(ClaimsPrincipal user) => Roles.All.Where(user.IsInRole).ToList();
}

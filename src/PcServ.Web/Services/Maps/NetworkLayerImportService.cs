using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;

namespace PcServ.Web.Services.Maps;

public enum NetworkLayerStatus
{
    Ready,
    AlreadyAdded,
    NoMatchingMap
}

public sealed record NetworkLayerPreview(string File, NetworkLayerStatus Status);

public sealed record NetworkLayerResult(int Maps, int Items, int ToCheck, IReadOnlyList<string> Failures);

/// <summary>
/// Adds access points and closets to existing maps, from one JSON file per map made by a separate map reader.
/// They go in a "Network_Layers" folder beside the import list.
/// Each map's image is redrawn without its colored markings, so a layer that's off truly disappears.
/// </summary>
public sealed class NetworkLayerImportService(MapImportOptions options, AppDbFactory dbFactory, MapFileStore files)
{
    public const string AccessPointsLayerName = "Wi-Fi access points";
    public const string ClosetsLayerName = "Network closets";
    public const string MissingSymbolNote = "A name was found with no symbol near it. This marker sits on the name, not the access point. Use Move items to place it.";
    public const string CheckNameNote = "Placed by hand. The name was read automatically, so check it.";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private string Folder => Path.Combine(Path.GetDirectoryName(options.ListPath)!, "Network_Layers");

    private sealed record LayerFile(string? File, List<ItemDto>? Features, List<StrayDto>? StrayNames);
    private sealed record ItemDto(double X, double Y, string? Symbol, string? Label, List<string>? Flags);
    private sealed record StrayDto(string? Text, double X, double Y);

    /// <summary>Checks which layer files can be applied, without changing anything.</summary>
    public async Task<List<NetworkLayerPreview>> PreviewAsync()
    {
        var preview = new List<NetworkLayerPreview>();
        if (!Directory.Exists(Folder)) return preview;

        await using var db = await dbFactory.CreateAsync();
        var maps = await db.SiteMaps.AsNoTracking()
            .Select(m => new { m.SourceFileName, HasNetwork = m.Layers.Any(l => l.Kind == MapLayerKind.Network) })
            .ToListAsync();

        foreach (var path in Directory.GetFiles(Folder, "*.json").OrderBy(p => p))
        {
            var file = Read(path)?.File ?? "";
            var map = maps.FirstOrDefault(m => string.Equals(m.SourceFileName, file, StringComparison.OrdinalIgnoreCase));
            preview.Add(new NetworkLayerPreview(file,
                map is null ? NetworkLayerStatus.NoMatchingMap
                : map.HasNetwork ? NetworkLayerStatus.AlreadyAdded
                : NetworkLayerStatus.Ready));
        }
        return preview;
    }

    /// <summary>Applies every ready file. One failure is recorded and doesn't stop the rest; running it again carries on.</summary>
    public async Task<NetworkLayerResult> ImportAsync(IProgress<(int Done, int Total, string Current)>? progress = null, CancellationToken ct = default)
    {
        var ready = (await PreviewAsync()).Where(p => p.Status == NetworkLayerStatus.Ready).Select(p => p.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.json").OrderBy(p => p).ToList() : [];
        var failures = new List<string>();
        int done = 0, mapsDone = 0, items = 0, toCheck = 0;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var data = Read(path);
            if (data?.File is null || !ready.Contains(data.File)) continue;

            progress?.Report((done, ready.Count, data.File));
            try
            {
                var (count, check) = await ApplyAsync(data, ct);
                mapsDone++;
                items += count;
                toCheck += check;
            }
            catch (Exception ex) when (ex is MapFileException or IOException or InvalidOperationException or DbUpdateException)
            {
                failures.Add($"{data.File}: {ex.Message}");
            }
            done++;
            progress?.Report((done, ready.Count, data.File));
        }

        return new NetworkLayerResult(mapsDone, items, toCheck, failures);
    }

    private async Task<(int Items, int ToCheck)> ApplyAsync(LayerFile data, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync();
        var map = await db.SiteMaps.FirstAsync(m => m.SourceFileName == data.File, ct);
        var sourcePath = files.SourcePdfPath(map.StorageKey) ?? throw new InvalidOperationException("The original PDF is missing.");

        var pdf = await File.ReadAllBytesAsync(sourcePath, ct);
        var image = await Task.Run(() => MapPdfReader.RenderPdfWithoutColor(pdf), ct);

        var features = (data.Features ?? []).Where(f => f.Symbol is not null).Select(ToFeature).ToList();

        // A name with no symbol near it means a symbol was missed, so it's kept for a person to place or remove.
        foreach (var stray in data.StrayNames ?? [])
        {
            if (string.IsNullOrWhiteSpace(stray.Text)) continue;
            var text = stray.Text.Trim().ToUpperInvariant();
            features.Add(new MapFeature
            {
                Type = MapFeatureType.Marker,
                X = stray.X,
                Y = stray.Y,
                Label = text,
                Symbol = text.Contains("EXT") ? MapSymbols.OutdoorAccessPoint : MapSymbols.IndoorAccessPoint,
                NeedsReview = true,
                Notes = MissingSymbolNote,
            });
        }

        var accessPoints = features.Where(f => f.Symbol is not (MapSymbols.Mdf or MapSymbols.Idf)).ToList();
        var closets = features.Where(f => f.Symbol is MapSymbols.Mdf or MapSymbols.Idf).ToList();

        db.SuppressEntityAudit = true;
        var order = await db.MapLayers.Where(l => l.SiteMapId == map.Id).Select(l => (int?)l.SortOrder).MaxAsync(ct) ?? -1;
        db.MapLayers.Add(new MapLayer { SiteMapId = map.Id, Name = AccessPointsLayerName, Kind = MapLayerKind.Network, MinimumRole = Roles.Tech, Color = "#4F94EF", SortOrder = order + 1, Features = accessPoints });
        db.MapLayers.Add(new MapLayer { SiteMapId = map.Id, Name = ClosetsLayerName, Kind = MapLayerKind.Network, MinimumRole = Roles.Tech, Color = "#F09600", SortOrder = order + 2, Features = closets });

        var toCheck = features.Count(f => f.NeedsReview);
        db.LogAudit("Map.NetworkLayersAdded", nameof(SiteMap), map.Id.ToString(),
            $"{map.Name}: {accessPoints.Count} access point(s), {closets.Count} closet(s), {toCheck} to check.");
        await db.SaveChangesAsync(ct);

        // The image changes last, so a failed save leaves the map exactly as it was.
        await files.ReplaceBaseImageAsync(map.StorageKey, image.Png);
        return (features.Count, toCheck);
    }

    private static MapFeature ToFeature(ItemDto item) => new()
    {
        Type = MapFeatureType.Marker,
        X = Math.Clamp(item.X, 0, 1),
        Y = Math.Clamp(item.Y, 0, 1),
        Label = item.Label,
        Symbol = item.Symbol,
        NeedsReview = item.Flags is { Count: > 0 },
        Notes = item.Flags is { Count: > 0 } ? string.Join("; ", item.Flags) : null,
    };

    private static LayerFile? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<LayerFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

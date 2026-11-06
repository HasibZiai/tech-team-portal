using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using SkiaSharp;

// SkiaSharp supports the platforms this app runs on (Windows and Linux).
#pragma warning disable CA1416

namespace PcServ.Web.Services.Maps;

public sealed record MapPhotoView(int Id, string FileName, string? Caption);

public sealed record ProcessedPhoto(byte[] Jpeg, byte[] Thumbnail, int Width, int Height);

/// <summary>
/// Photos on map items, and pins for adding photos and notes anywhere on a map. Pictures are checked
/// against the same role rules as the item they're on, every time one is requested.
/// </summary>
public sealed class MapPhotoService(AppDbFactory dbFactory, TestVersion testVersion, MapFileStore files)
{
    public const string PinLayerName = "Photos and notes";
    public const string NewPinName = "New photo or note";

    public const long MaxUploadBytes = 25 * 1024 * 1024;
    private const int MaxSide = 2400, ThumbnailSide = 480, JpegQuality = 85;

    /// <summary>Refuses images so large that opening them could exhaust the server's memory.</summary>
    private const long MaxPixels = 100_000_000;

    /// <summary>Adds a pin to a map, creating its Photos and notes layer the first time. Restricted to Techs and above by default.</summary>
    public async Task<(MapLayerView Layer, MapFeatureView Feature)> CreatePinAsync(int mapId, double x, double y, ClaimsPrincipal user)
    {
        RequireEdit(user);
        await using var db = await dbFactory.CreateAsync();
        var map = await db.SiteMaps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mapId) ?? throw new InvalidOperationException("That map no longer exists.");
        if (!Roles.MeetsMinimum(HeldRoles(user), map.MinimumRole)) throw new InvalidOperationException("You don't have permission to change this map.");

        var layer = await db.MapLayers.FirstOrDefaultAsync(l => l.SiteMapId == mapId && l.Kind == MapLayerKind.Custom && l.Name == PinLayerName);
        if (layer is null)
        {
            var order = await db.MapLayers.Where(l => l.SiteMapId == mapId).Select(l => (int?)l.SortOrder).MaxAsync() ?? -1;
            layer = new MapLayer { SiteMapId = mapId, Name = PinLayerName, Kind = MapLayerKind.Custom, MinimumRole = Roles.Tech, Color = "#12315C", SortOrder = order + 1 };
            db.MapLayers.Add(layer);
        }

        var feature = new MapFeature
        {
            MapLayer = layer,
            Type = MapFeatureType.Marker,
            X = Math.Clamp(x, 0, 1),
            Y = Math.Clamp(y, 0, 1),
            Label = NewPinName,
            Symbol = MapSymbols.PhotoPin,
        };
        db.MapFeatures.Add(feature);
        await db.SaveChangesAsync();

        return (new MapLayerView(layer.Id, layer.Name, layer.Kind.ToString(), layer.MinimumRole, layer.Color, layer.VisibleByDefault, []),
            new MapFeatureView(feature.Id, feature.Type.ToString(), feature.X, feature.Y, feature.Label, feature.Symbol, null, false));
    }

    public async Task<List<MapPhotoView>> ListAsync(int featureId)
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.MapPhotos.AsNoTracking().Where(p => p.MapFeatureId == featureId).OrderBy(p => p.Id)
            .Select(p => new MapPhotoView(p.Id, p.FileName, p.Caption)).ToListAsync();
    }

    public async Task<int> CountForFeatureAsync(int featureId)
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.MapPhotos.CountAsync(p => p.MapFeatureId == featureId);
    }

    /// <summary>Adds a photo to an item. It's re-saved as a new JPEG, so only image pixels are ever stored.</summary>
    public async Task<MapPhotoView> AddAsync(int featureId, string fileName, byte[] upload, string? caption, ClaimsPrincipal user)
    {
        RequireEdit(user);
        await using var db = await dbFactory.CreateAsync();
        var feature = await db.MapFeatures.Include(f => f.MapLayer).ThenInclude(l => l!.SiteMap).FirstOrDefaultAsync(f => f.Id == featureId)
            ?? throw new InvalidOperationException("That item no longer exists.");
        RequireAccess(feature, user);

        var processed = await Task.Run(() => Process(upload));
        var key = MapFileStore.NewStorageKey();
        await files.SavePhotoAsync(feature.MapLayer!.SiteMap!.StorageKey, key, processed.Jpeg, processed.Thumbnail);

        try
        {
            var photo = new MapPhoto
            {
                MapFeatureId = featureId,
                StorageKey = key,
                FileName = Truncate(FileNames.WithoutFolders(fileName), 260),
                Caption = string.IsNullOrWhiteSpace(caption) ? null : Truncate(caption.Trim(), 300),
                Width = processed.Width,
                Height = processed.Height,
            };
            db.MapPhotos.Add(photo);
            await db.SaveChangesAsync();
            return new MapPhotoView(photo.Id, photo.FileName, photo.Caption);
        }
        catch
        {
            // Don't leave a picture behind that nothing refers to.
            files.DeletePhoto(feature.MapLayer.SiteMap.StorageKey, key);
            throw;
        }
    }

    public async Task DeleteAsync(int photoId, ClaimsPrincipal user)
    {
        RequireEdit(user);
        await using var db = await dbFactory.CreateAsync();
        var photo = await db.MapPhotos.Include(p => p.MapFeature).ThenInclude(f => f!.MapLayer).ThenInclude(l => l!.SiteMap).FirstOrDefaultAsync(p => p.Id == photoId);
        if (photo is null) return;
        RequireAccess(photo.MapFeature!, user);

        var (mapKey, key) = (photo.MapFeature!.MapLayer!.SiteMap!.StorageKey, photo.StorageKey);
        db.MapPhotos.Remove(photo);
        await db.SaveChangesAsync();
        files.DeletePhoto(mapKey, key);
    }

    /// <summary>
    /// A photo's file, or null if it doesn't exist or this user can't see its item. For a web endpoint,
    /// which doesn't have the Blazor sign-in context AppDbFactory uses. This read has nothing to audit.
    /// </summary>
    public async Task<string?> PhotoPathAsync(int photoId, bool thumbnail, ClaimsPrincipal user)
    {
        await using var db = await testVersion.CreateForAsync(user);
        var photo = await db.MapPhotos.AsNoTracking().Where(p => p.Id == photoId)
            .Select(p => new { p.StorageKey, MapKey = p.MapFeature!.MapLayer!.SiteMap!.StorageKey, LayerRole = p.MapFeature.MapLayer.MinimumRole, MapRole = p.MapFeature.MapLayer.SiteMap.MinimumRole })
            .FirstOrDefaultAsync();
        if (photo is null) return null;

        var held = HeldRoles(user);
        if (!Roles.MeetsMinimum(held, photo.LayerRole) || !Roles.MeetsMinimum(held, photo.MapRole)) return null;
        return files.PhotoPath(photo.MapKey, photo.StorageKey, thumbnail);
    }

    /// <summary>
    /// Turns an upload into a clean JPEG and thumbnail: turned the right way up, resized, and without the
    /// original file's hidden data, such as the GPS location a phone records.
    /// </summary>
    public static ProcessedPhoto Process(byte[] upload)
    {
        if (upload.Length == 0) throw new MapFileException("That file is empty.");
        if (upload.Length > MaxUploadBytes) throw new MapFileException("That photo is too large. The limit is 25 MB.");

        using var data = SKData.CreateCopy(upload);
        using var codec = SKCodec.Create(data)
            ?? throw new MapFileException("That file couldn't be read as a picture. Use a JPEG, PNG or WebP. iPhone HEIC photos need converting to JPEG first.");
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels) throw new MapFileException("That picture has too many pixels. Please use a smaller one.");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new MapFileException("That picture couldn't be read. It may be damaged.");
        }

        using var upright = Upright(decoded, codec.EncodedOrigin);
        var (jpeg, width, height) = Encode(upright, MaxSide);
        var (thumbnail, _, _) = Encode(upright, ThumbnailSide);
        return new ProcessedPhoto(jpeg, thumbnail, width, height);
    }

    // Phones often store pictures sideways with a note saying which way is up, and that note is removed on saving.
    private static SKBitmap Upright(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        float w = source.Width, h = source.Height;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
        }
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return result;
    }

    private static (byte[] Jpeg, int Width, int Height) Encode(SKBitmap source, int longSide)
    {
        var scale = Math.Min(1.0, (double)longSide / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale)), height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White); // JPEG has no transparency.
        using var image = SKImage.FromBitmap(source);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return (encoded.ToArray(), width, height);
    }

    private static void RequireEdit(ClaimsPrincipal user)
    {
        if (!Roles.MeetsMinimum(HeldRoles(user), Roles.Tech)) throw new InvalidOperationException("You don't have permission to change photos.");
    }

    private static void RequireAccess(MapFeature feature, ClaimsPrincipal user)
    {
        var held = HeldRoles(user);
        if (!Roles.MeetsMinimum(held, feature.MapLayer!.MinimumRole) || !Roles.MeetsMinimum(held, feature.MapLayer.SiteMap!.MinimumRole))
        {
            throw new InvalidOperationException("You don't have permission to change that item.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static List<string> HeldRoles(ClaimsPrincipal user) => Roles.All.Where(user.IsInRole).ToList();
}

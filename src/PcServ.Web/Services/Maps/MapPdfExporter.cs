using System.Globalization;
using System.Security.Claims;
using System.Text;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

// SkiaSharp supports the platforms this app runs on (Windows and Linux).
#pragma warning disable CA1416

namespace PcServ.Web.Services.Maps;

public sealed record MapPdf(string FileName, byte[] Content);

/// <summary>Makes a PDF of what someone is allowed to see on a map, and records that it left the app.</summary>
public sealed class MapExportService(SiteMapService maps, AppDbFactory dbFactory)
{
    /// <summary>Returns null if this user can't open the map. Layers they can't see are never included, whatever is asked for.</summary>
    public async Task<MapPdf?> CreatePdfAsync(int mapId, IReadOnlySet<int> layerIds, MapArea area, string printedOn, ClaimsPrincipal user)
    {
        var map = await maps.GetAsync(mapId, user);
        if (map is null) return null;
        var path = await maps.BaseImagePathAsync(mapId, user) ?? throw new MapFileException("This map's picture is missing, so it can't be saved as a PDF.");

        var image = await File.ReadAllBytesAsync(path);
        var content = await Task.Run(() => MapPdfExporter.Build(map, image, layerIds, area, user.Identity?.Name ?? "Unknown", printedOn));

        var shown = map.Layers.Where(l => layerIds.Contains(l.Id)).Select(l => l.Name).ToList();
        await using var db = await dbFactory.CreateAsync();
        db.LogAudit("Map.ExportedPdf", nameof(SiteMap), map.Id.ToString(), $"Saved {map.SiteName}: {map.Name} as a PDF. Layers: {(shown.Count == 0 ? "none" : string.Join(", ", shown))}.");
        await db.SaveChangesAsync();

        return new MapPdf(MapPdfExporter.FileNameFor(map), content);
    }
}

/// <summary>
/// Draws a map on one tabloid page: the map's upright picture with its layers on top, so rotated source
/// PDFs don't matter. Text uses the standard PDF fonts, which PDF readers supply, so the server needs no fonts installed.
/// </summary>
public static class MapPdfExporter
{
    private const double LongSide = 1224, ShortSide = 792, Margin = 28;
    private const double HeaderHeight = 44, FooterHeight = 26, LegendRowHeight = 15;
    private const double SymbolSize = 11, LabelSize = 8, NameSize = 6, MaxTextLength = 160;

    /// <summary>How much labels and symbols can grow on a close-up, where the drawing itself is enlarged.</summary>
    private const double MaxZoom = 3;

    private const string RestrictedNotice = "Contains restricted information. Do not share outside your organization.";

    public static byte[] Build(MapView map, byte[] basePng, IReadOnlySet<int> layerIds, MapArea area, string printedBy, string printedOn)
    {
        var layers = map.Layers.Where(l => layerIds.Contains(l.Id)).ToList();
        var items = layers.SelectMany(l => l.Features.Where(f => area.Contains(f.X, f.Y)).Select(f => (Layer: l, Feature: f))).ToList();

        var areaRatio = area.Width * map.ImageWidth / (area.Height * map.ImageHeight);
        var (pageWidth, pageHeight) = areaRatio >= 1 ? (LongSide, ShortSide) : (ShortSide, LongSide);

        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = Clean($"{map.SiteName}: {map.Name}");
        builder.DocumentInformation.Producer = "Tech Team Portal";
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(pageWidth, pageHeight);

        double Measure(string text, double size, PdfDocumentBuilder.AddedFont f)
        {
            var letters = page.MeasureText(text, size, new PdfPoint(0, 0), f);
            return letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X;
        }

        // Header
        page.SetTextAndFillColor(18, 49, 92);
        page.AddText(Clean(map.SiteName), 16, new PdfPoint(Margin, pageHeight - Margin - 14), bold);
        page.AddText(Clean(map.Name), 10, new PdfPoint(Margin, pageHeight - Margin - 29), font);
        page.ResetColor();

        // The legend goes under the map, and has as many rows as it needs.
        var entries = layers.SelectMany(l => l.Legend ?? []).ToDictionary(e => e.Id);
        var legendItems = items.Select(i => i.Feature.Symbol).OfType<string>().Distinct().OrderBy(MapSymbols.DisplayName)
            .Select(s => (Symbol: (string?)s, Entry: (MapLegendEntryView?)null, Name: MapSymbols.DisplayName(s)))
            .Concat(entries.Values.Where(e => items.Any(i => i.Feature.LegendEntryId == e.Id)).Select(e => (Symbol: (string?)null, Entry: (MapLegendEntryView?)e, e.Name)))
            .ToList();
        var legendRows = new List<List<(string? Symbol, MapLegendEntryView? Entry, string Text)>>();
        var rowWidth = double.MaxValue;
        foreach (var (symbol, entry, name) in legendItems)
        {
            var text = Clean(name);
            var width = SymbolSize + 4 + Measure(text, 9, font) + 16;
            if (rowWidth + width > pageWidth - 2 * Margin)
            {
                legendRows.Add([]);
                rowWidth = 0;
            }
            legendRows[^1].Add((symbol, entry, text));
            rowWidth += width;
        }
        var legendHeight = legendRows.Count * LegendRowHeight + (legendRows.Count > 0 ? 6 : 0);

        // The map, as large as fits between the header and everything below it.
        var spaceWidth = pageWidth - 2 * Margin;
        var spaceBottom = Margin + FooterHeight + legendHeight;
        var spaceHeight = pageHeight - Margin - HeaderHeight - spaceBottom;
        var scale = Math.Min(spaceWidth / areaRatio, spaceHeight);
        double drawHeight = scale, drawWidth = scale * areaRatio;
        var left = Margin + (spaceWidth - drawWidth) / 2;
        var bottom = spaceBottom + (spaceHeight - drawHeight) / 2;

        page.AddPng(Crop(basePng, area), new PdfRectangle(left, bottom, left + drawWidth, bottom + drawHeight));
        page.SetStrokeColor(136, 146, 160);
        page.DrawRectangle(new PdfPoint(left, bottom), drawWidth, drawHeight, .75);
        page.ResetColor();

        var icons = new Dictionary<string, PdfPageBuilder.AddedImage>();
        void DrawSymbol(string? symbol, string fallbackColor, double centerX, double centerY, double size)
        {
            var box = new PdfRectangle(centerX - size / 2, centerY - size / 2, centerX + size / 2, centerY + size / 2);
            var key = symbol ?? "";
            if (symbol is MapSymbols.IndoorAccessPoint or MapSymbols.OutdoorAccessPoint or MapSymbols.OutdoorAccessPoint577 or MapSymbols.PhotoPin or MapSymbols.OrderPin)
            {
                if (icons.TryGetValue(key, out var added)) page.AddImage(added, box);
                else icons[key] = page.AddPng(SymbolIcon(symbol), box);
                return;
            }

            var (r, g, b) = symbol switch
            {
                MapSymbols.Mdf => ((byte)0xF0, (byte)0x96, (byte)0x00),
                MapSymbols.Idf => ((byte)0xF5, (byte)0xD8, (byte)0x00),
                _ => ParseColor(fallbackColor),
            };
            page.SetTextAndFillColor(r, g, b);
            page.SetStrokeColor(27, 39, 53);
            page.DrawRectangle(new PdfPoint(box.Left, box.Bottom), size, size, .5, fill: true);
            page.ResetColor();
            var letter = symbol switch { MapSymbols.Mdf => "M", MapSymbols.Idf => "I", _ => "" };
            if (letter.Length > 0)
            {
                page.SetTextAndFillColor(27, 39, 53);
                var letterSize = size * .64;
                page.AddText(letter, letterSize, new PdfPoint(centerX - Measure(letter, letterSize, bold) / 2, centerY - letterSize * .36), bold);
                page.ResetColor();
            }
        }

        void DrawCustom(MapLegendEntryView entry, double centerX, double centerY, double size)
        {
            var box = new PdfRectangle(centerX - size / 2, centerY - size / 2, centerX + size / 2, centerY + size / 2);
            var key = $"custom:{entry.Id}";
            if (icons.TryGetValue(key, out var added)) page.AddImage(added, box);
            else icons[key] = page.AddPng(CustomIcon(entry), box);
        }

        double PageX(double x) => left + (x - area.Left) / area.Width * drawWidth;
        double PageY(double y) => bottom + drawHeight * (1 - (y - area.Top) / area.Height);

        var zoom = Math.Clamp(1 / Math.Max(area.Width, area.Height), 1, MaxZoom);
        double symbolSize = SymbolSize * zoom, labelSize = LabelSize * zoom, nameSize = NameSize * zoom;

        // Symbols first, so that text stays readable on top of them.
        foreach (var (layer, feature) in items.Where(i => i.Feature.Type == nameof(MapFeatureType.Marker)))
        {
            if (feature.LegendEntryId is { } entryId && entries.TryGetValue(entryId, out var entry)) DrawCustom(entry, PageX(feature.X), PageY(feature.Y), symbolSize);
            else DrawSymbol(feature.Symbol, layer.Color, PageX(feature.X), PageY(feature.Y), symbolSize);
        }

        page.SetTextAndFillColor(27, 39, 53);
        foreach (var (_, feature) in items)
        {
            var text = Clean(feature.Label ?? "");
            if (text.Length == 0) continue;
            double x = PageX(feature.X), y = PageY(feature.Y);
            if (feature.Type == nameof(MapFeatureType.Marker)) page.AddText(text, nameSize, new PdfPoint(x + symbolSize / 2 + 1.5, y - nameSize / 3), font);
            else page.AddText(text, labelSize, new PdfPoint(x - Measure(text, labelSize, font) / 2, y - labelSize / 3), font);
        }
        page.ResetColor();

        // Legend
        var legendY = spaceBottom - 6 - LegendRowHeight + 3;
        foreach (var row in legendRows)
        {
            var x = Margin;
            foreach (var (symbol, entry, text) in row)
            {
                if (entry is not null) DrawCustom(entry, x + SymbolSize / 2, legendY + 3, SymbolSize);
                else DrawSymbol(symbol, "#12315C", x + SymbolSize / 2, legendY + 3, SymbolSize);
                page.AddText(text, 9, new PdfPoint(x + SymbolSize + 4, legendY), font);
                x += SymbolSize + 4 + Measure(text, 9, font) + 16;
            }
            legendY -= LegendRowHeight;
        }

        // Footer
        var layerText = layers.Count == 0 ? "none" : string.Join(", ", layers.Select(l => l.Name));
        page.AddText(Clean($"Layers shown: {layerText}"), 8, new PdfPoint(Margin, Margin + 13), font);
        page.AddText(Clean($"Saved by {printedBy} on {printedOn} from Tech Team Portal."), 8, new PdfPoint(Margin, Margin + 2), font);
        if (map.MinimumRole != Roles.Viewer || layers.Any(l => l.MinimumRole != Roles.Viewer))
        {
            page.SetTextAndFillColor(18, 49, 92);
            page.AddText(RestrictedNotice, 8, new PdfPoint(pageWidth - Margin - Measure(RestrictedNotice, 8, bold), Margin + 2), bold);
            page.ResetColor();
        }

        return builder.Build();
    }

    public static string FileNameFor(MapView map)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new string($"{map.SiteName} - {map.Name}".Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return (name.Length == 0 ? "Site map" : name[..Math.Min(name.Length, 150)]) + ".pdf";
    }

    /// <summary>
    /// Plain letters only. The standard PDF fonts can't be relied on past ASCII (an accented e came out as a
    /// different letter), so accents are dropped, and anything else that can't be written becomes a question mark.
    /// </summary>
    private static string Clean(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings(" ").Normalize(NormalizationForm.FormD))
        {
            if (result.Length >= MaxTextLength) break;
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            result.Append(c switch
            {
                >= ' ' and <= '~' => c,
                '\u00A0' => ' ',
                '\u2013' or '\u2014' or '\u2212' => '-',
                '\u2018' or '\u2019' => '\'',
                '\u201C' or '\u201D' => '"',
                _ => '?',
            });
        }
        return result.ToString().Trim();
    }

    private static byte[] Crop(byte[] png, MapArea area)
    {
        if (area.IsWhole) return png;
        using var source = SKBitmap.Decode(png) ?? throw new MapFileException("This map's picture couldn't be read.");
        var box = SKRectI.Round(new SKRect((float)(area.Left * source.Width), (float)(area.Top * source.Height), (float)(area.Right * source.Width), (float)(area.Bottom * source.Height)));
        box.Intersect(new SKRectI(0, 0, source.Width, source.Height));
        using var cropped = new SKBitmap();
        if (box.Width < 1 || box.Height < 1 || !source.ExtractSubset(cropped, box)) throw new MapFileException("That part of the map couldn't be saved.");
        using var image = SKImage.FromBitmap(cropped);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] CustomIcon(MapLegendEntryView entry)
    {
        const int Pixels = 96;
        using var surface = SKSurface.Create(new SKImageInfo(Pixels, Pixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(Pixels / 24f);
        using var path = SKPath.ParseSvgPathData(MapShapes.Path(entry.Shape));
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColor.TryParse(entry.Color, out var color) ? color : SKColor.Parse("#12315C") };
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.White };
        surface.Canvas.DrawPath(path, fill);
        surface.Canvas.DrawPath(path, outline);
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    // The same shapes and colors as the map on screen (see sitemap.js).
    private static byte[] SymbolIcon(string symbol)
    {
        const int Pixels = 96;
        const string Star = "M12 2l3 6.6 7.2.8-5.3 4.8 1.5 7.1L12 17.7 5.6 21.3l1.5-7.1L1.8 9.4 9 8.6z";
        const string Triangle = "M12 3l10 18H2z";
        const string Camera = "M9.2 6.5 8 8H5.5A1.5 1.5 0 0 0 4 9.5v7A1.5 1.5 0 0 0 5.5 18h13a1.5 1.5 0 0 0 1.5-1.5v-7A1.5 1.5 0 0 0 18.5 8H16l-1.2-1.5zM12 15.8a3 3 0 1 1 0-6 3 3 0 0 1 0 6z";

        const string Box = "M12 4 5 7.5v9l7 3.5 7-3.5v-9zm0 2 4.2 2.1L12 10.2 7.8 8.1zM7 9.7l4 2v5.6l-4-2zm10 0v5.6l-4 2v-5.6z";

        using var surface = SKSurface.Create(new SKImageInfo(Pixels, Pixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(Pixels / 24f);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColor.Parse("#1B2735") };

        if (symbol is MapSymbols.PhotoPin or MapSymbols.OrderPin)
        {
            var color = SKColor.Parse(symbol == MapSymbols.OrderPin ? "#0B7A75" : "#12315C");
            fill.Color = SKColors.White;
            canvas.DrawCircle(12, 12, 11.5f, fill);
            outline.Color = color;
            canvas.DrawCircle(12, 12, 11.5f, outline);
            fill.Color = color;
            using var picture = SKPath.ParseSvgPathData(symbol == MapSymbols.OrderPin ? Box : Camera);
            canvas.DrawPath(picture, fill);
        }
        else
        {
            fill.Color = SKColor.Parse(symbol switch
            {
                MapSymbols.IndoorAccessPoint => "#4F94EF",
                MapSymbols.OutdoorAccessPoint577 => "#B8861E",
                _ => "#1FB524",
            });
            using var path = SKPath.ParseSvgPathData(symbol == MapSymbols.IndoorAccessPoint ? Star : Triangle);
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, outline);
        }

        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static (byte R, byte G, byte B) ParseColor(string color) =>
        SKColor.TryParse(color, out var parsed) ? (parsed.Red, parsed.Green, parsed.Blue) : ((byte)0x12, (byte)0x31, (byte)0x5C);
}

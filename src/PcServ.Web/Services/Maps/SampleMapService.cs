using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PcServ.Web.Services.Maps;

/// <summary>
/// A fictional school for demos and tests. The layout is drawn from scratch here and isn't based
/// on any real building, so it's safe to show anyone and safe to publish with the source code.
/// </summary>
public sealed class SampleMapService(SiteMapService maps, AppDbFactory dbFactory)
{
    public const string SampleSourceFileName = "sample-site-plan.pdf";
    public const string SampleSiteName = "Maple Grove Elementary";

    private const double PageWidth = 1224;
    private const double PageHeight = 792;

    private sealed record Room(string Label, double X, double Y, double W, double H, string ApCode)
    {
        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;
    }

    private static readonly Room[] Rooms =
    [
        // West classroom wing, either side of a corridor.
        new("Rm 1", 150, 470, 100, 90, "RM01"), new("Rm 2", 250, 470, 100, 90, "RM02"), new("Rm 3", 350, 470, 100, 90, "RM03"),
        new("Rm 4", 150, 350, 100, 90, "RM04"), new("Rm 5", 250, 350, 100, 90, "RM05"), new("Rm 6", 350, 350, 100, 90, "RM06"),
        // Middle of the school.
        new("Science", 470, 520, 140, 90, "SCI"), new("Library", 470, 400, 140, 110, "LIB"), new("Office", 470, 300, 140, 90, "OFF"),
        new("MPR", 620, 400, 170, 160, "MPR"), new("Kinder", 620, 280, 170, 110, "KIN"),
        // East classroom wing.
        new("Rm 7", 800, 470, 100, 90, "RM07"), new("Rm 8", 900, 470, 100, 90, "RM08"), new("Rm 9", 1000, 470, 100, 90, "RM09"),
        new("Rm 10", 800, 350, 100, 90, "RM10"), new("Rm 11", 900, 350, 100, 90, "RM11"), new("Rm 12", 1000, 350, 100, 90, "RM12"),
        // Portable classrooms.
        new("P-1", 150, 640, 90, 70, "P01"), new("P-2", 250, 640, 90, 70, "P02"), new("P-3", 350, 640, 90, 70, "P03"), new("P-4", 450, 640, 90, 70, "P04"),
    ];

    private static readonly (string Code, double X, double Y)[] OutdoorAps =
    [
        ("EXT-01", 140, 455), ("EXT-02", 460, 625), ("EXT-03", 800, 575), ("EXT-04", 1110, 455), ("EXT-05", 610, 268),
    ];

    private static readonly (string Label, string Symbol, double X, double Y)[] Closets =
    [
        ("MDF", MapSymbols.Mdf, 590, 320), ("IDF-1", MapSymbols.Idf, 440, 445), ("IDF-2", MapSymbols.Idf, 810, 445), ("IDF-3", MapSymbols.Idf, 345, 632),
    ];

    /// <summary>True if the sample map has already been added.</summary>
    public async Task<bool> ExistsAsync()
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.SiteMaps.AnyAsync(m => m.SourceFileName == SampleSourceFileName);
    }

    /// <summary>Adds the sample map, and its fictional school if needed. Returns null if it already exists.</summary>
    public async Task<MapUploadResult?> AddAsync()
    {
        if (await ExistsAsync()) return null;

        int siteId;
        await using (var db = await dbFactory.CreateAsync())
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Name == SampleSiteName);
            if (site is null)
            {
                site = new Site { Name = SampleSiteName, Code = "MGE", Type = SiteType.Elementary, Address = "455 Maple Grove Rd" };
                db.Sites.Add(site);
                await db.SaveChangesAsync();
            }
            siteId = site.Id;
        }

        var pdf = CreatePdf();
        var image = MapPdfReader.RenderPdf(pdf, includeAnnotations: false);
        var labels = Rooms.Select(r => new ImportedText(r.Label, r.CenterX / PageWidth, ToY(r.CenterY + 22))).ToList();

        var upload = new MapUpload(siteId, null, "Site plan (sample)", SiteMapKind.SitePlan, null, SampleSourceFileName, pdf);
        return await maps.SaveMapAsync(upload, image, new MapPdfContents(labels, [], 1, 0), NetworkLayers());
    }

    private static IEnumerable<MapLayer> NetworkLayers()
    {
        var accessPoints = Rooms.Select(r => Marker(r.CenterX, r.CenterY - 8, $"MG-{r.ApCode}-AP", MapSymbols.IndoorAccessPoint))
            .Concat(OutdoorAps.Select(a => Marker(a.X, a.Y, $"MG-{a.Code}-AP", MapSymbols.OutdoorAccessPoint)))
            .ToList();

        yield return new MapLayer
        {
            Name = "Wi-Fi access points",
            Kind = MapLayerKind.Network,
            MinimumRole = Roles.Tech,
            Color = "#4F94EF",
            SortOrder = 2,
            Features = accessPoints,
        };

        yield return new MapLayer
        {
            Name = "Network closets",
            Kind = MapLayerKind.Network,
            MinimumRole = Roles.Tech,
            Color = "#F09600",
            SortOrder = 3,
            Features = Closets.Select(c => Marker(c.X, c.Y, c.Label, c.Symbol)).ToList(),
        };
    }

    private static MapFeature Marker(double x, double y, string label, string symbol) => new()
    {
        Type = MapFeatureType.Marker,
        X = x / PageWidth,
        Y = ToY(y),
        Label = label,
        Symbol = symbol,
    };

    private static double ToY(double pdfY) => 1 - pdfY / PageHeight;

    /// <summary>Draws the fictional school as a vector PDF, like a CAD plot.</summary>
    public static byte[] CreatePdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageWidth, PageHeight);

        void Line(double x1, double y1, double x2, double y2, double width = 0.8) =>
            page.DrawLine(new PdfPoint(x1, y1), new PdfPoint(x2, y2), width);

        void Box(double x, double y, double w, double h, double width = 0.8) =>
            page.DrawRectangle(new PdfPoint(x, y), w, h, width);

        void Circle(double cx, double cy, double r, double width = 0.6)
        {
            const int Sides = 24;
            for (var i = 0; i < Sides; i++)
            {
                double a = 2 * Math.PI * i / Sides, b = 2 * Math.PI * (i + 1) / Sides;
                Line(cx + r * Math.Cos(a), cy + r * Math.Sin(a), cx + r * Math.Cos(b), cy + r * Math.Sin(b), width);
            }
        }

        // Property line.
        Box(30, 30, PageWidth - 60, PageHeight - 60, 1);

        // Street, driveway and parking.
        Line(30, 70, 1194, 70, 1.5);
        Line(30, 105, 1194, 105, 1.5);
        Box(150, 130, 660, 100);
        for (var x = 170; x < 800; x += 22) { Line(x, 130, x, 165); Line(x, 195, x, 230); }
        Line(460, 105, 460, 130);
        Line(500, 105, 500, 130);

        // Sidewalk and walkway to the front door.
        Line(150, 262, 1100, 262, 0.6);
        for (var y = 232; y < 300; y += 8) Line(525, y, 555, y, 0.5);

        // Rooms, with heavier walls, like a floor plan.
        foreach (var room in Rooms)
        {
            Box(room.X, room.Y, room.W, room.H, 1.8);
            // A simple door swing.
            Line(room.X + 10, room.Y, room.X + 10, room.Y + 14, 0.5);
            Line(room.X + 10, room.Y + 14, room.X + 24, room.Y, 0.5);
        }

        // Corridors and covered walkways.
        Box(150, 440, 320, 30, 0.6);
        Box(790, 440, 310, 30, 0.6);
        for (var x = 150; x < 470; x += 40) Line(x, 440, x, 470, 0.3);
        for (var x = 790; x < 1100; x += 40) Line(x, 440, x, 470, 0.3);

        // Restrooms and storage.
        foreach (var (x, y) in new[] { (420d, 300d), (610d, 240d) })
        {
            Box(x, y, 40, 40, 1.2);
            Line(x + 20, y, x + 20, y + 40, 0.6);
        }

        // Hard courts, field and playground.
        foreach (var x in new[] { 620d, 700d, 780d })
        {
            Box(x, 640, 70, 70, 0.6);
            Line(x + 35, 640, x + 35, 710, 0.4);
            Circle(x + 35, 675, 10, 0.4);
        }
        Box(880, 620, 240, 110, 0.8);
        Circle(1000, 675, 28, 0.6);
        Line(1000, 620, 1000, 730, 0.5);

        // Trees.
        foreach (var (x, y) in new[] { (70d, 700d), (70d, 620d), (70d, 540d), (70d, 460d), (70d, 380d), (70d, 300d), (1160d, 300d), (1160d, 380d), (1160d, 460d), (1160d, 540d), (850d, 200d), (900d, 170d), (960d, 205d), (1030d, 190d), (1100d, 205d) })
        {
            Circle(x, y, 18, 0.5);
            Circle(x, y, 1.5, 0.5);
        }

        // Title block and street name.
        page.AddText("Maple Grove Elementary", 18, new PdfPoint(960, 135), font);
        page.AddText("Fictional sample map", 11, new PdfPoint(960, 121), font);
        page.AddText("455 Maple Grove Rd", 11, new PdfPoint(960, 109), font);
        page.AddText("MAPLE GROVE RD", 14, new PdfPoint(520, 45), font);

        return builder.Build();
    }
}

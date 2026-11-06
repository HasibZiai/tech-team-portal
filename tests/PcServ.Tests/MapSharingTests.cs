using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;
using SkiaSharp;
using UglyToad.PdfPig;

#pragma warning disable CA1416

namespace PcServ.Tests;

public class MapViewLinkTests
{
    [Fact]
    public void A_link_round_trips_its_layers_and_area()
    {
        var link = MapViewLink.Build(7, [3, 12], new MapArea(.1, .2, .5, .75));

        Assert.Equal("maps/7?layers=3,12&view=0.1,0.2,0.5,0.75", link);
        var query = link[(link.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(new HashSet<int> { 3, 12 }, MapViewLink.ParseLayers(query["layers"]));
        Assert.Equal(new MapArea(.1, .2, .5, .75), MapViewLink.ParseArea(query["view"]));
    }

    [Fact]
    public void The_whole_map_needs_no_area_and_no_layers_stays_distinct_from_not_saying()
    {
        var link = MapViewLink.Build(7, [], MapArea.Whole);

        Assert.Equal("maps/7?layers=none", link);
        Assert.Empty(MapViewLink.ParseLayers("none")!);
        Assert.Null(MapViewLink.ParseLayers(null));
    }

    [Fact]
    public void Numbers_are_written_the_same_way_in_any_language()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.EndsWith("view=0.1,0.2,0.5,0.75", MapViewLink.Build(1, [1], new MapArea(.1, .2, .5, .75)));
            Assert.Equal(new MapArea(.1, .2, .5, .75), MapViewLink.ParseArea("0.1,0.2,0.5,0.75"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("0.5,0.5,0.2,0.2")]
    [InlineData("0.5,0.5,0.5,0.5")]
    [InlineData("NaN,0,1,1")]
    [InlineData("Infinity,0,1,1")]
    [InlineData("5,5,9,9")]
    public void Nonsense_areas_are_ignored(string value) => Assert.Null(MapViewLink.ParseArea(value));

    [Fact]
    public void A_layer_list_of_only_junk_counts_as_not_saying() => Assert.Null(MapViewLink.ParseLayers("abc,<script>"));

    [Fact]
    public void Areas_are_trimmed_to_the_map() => Assert.True(MapViewLink.ParseArea("-3,-3,9,9")!.IsWhole);

    [Fact]
    public void Junk_in_a_layer_list_is_ignored() =>
        Assert.Equal(new HashSet<int> { 4, 9 }, MapViewLink.ParseLayers("4,<script>,-2,1e5,9"));
}

public class MapPdfExportTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "pat"), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Tech = UserInRole(Roles.Tech);
    private static readonly ClaimsPrincipal Viewer = UserInRole(Roles.Viewer);

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static MapView Map(int width, int height, params MapLayerView[] layers) =>
        new(1, "Site plan", "Test School", null, SiteMapKind.SitePlan, null, Roles.Viewer, width, height, layers);

    private static MapFeatureView Marker(int id, string label, string symbol, double x = .5, double y = .5) =>
        new(id, nameof(MapFeatureType.Marker), x, y, label, symbol, null, false);

    private static MapFeatureView Label(int id, string label, double x = .5, double y = .5) =>
        new(id, nameof(MapFeatureType.Label), x, y, label, null, null, false);

    private static MapLayerView Layer(int id, string name, string role, params MapFeatureView[] features) =>
        new(id, name, "Custom", role, "#12315C", true, features);

    private static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Concat(document.GetPage(1).Letters.Select(l => l.Value));
    }

    private static byte[] Build(MapView map, IEnumerable<int> layers, MapArea? area = null) =>
        MapPdfExporter.Build(map, Png(map.ImageWidth, map.ImageHeight), layers.ToHashSet(), area ?? MapArea.Whole, "pat", "November 4, 2026");

    [Fact]
    public void Includes_the_title_chosen_layers_legend_and_who_saved_it()
    {
        var map = Map(800, 600,
            Layer(1, "Rooms", Roles.Viewer, Label(1, "Library")),
            Layer(2, "Closets", Roles.Tech, Marker(2, "IDF-7", MapSymbols.Idf)));

        var text = TextOf(Build(map, [1, 2]));

        foreach (var expected in new[] { "Test School", "Site plan", "Library", "IDF-7", "IDF", "Layers shown: Rooms, Closets", "Saved by pat on November 4, 2026" })
        {
            Assert.Contains(expected, text);
        }
    }

    [Fact]
    public void Layers_that_are_switched_off_are_left_out()
    {
        var map = Map(800, 600,
            Layer(1, "Rooms", Roles.Viewer, Label(1, "Library")),
            Layer(2, "Closets", Roles.Tech, Marker(2, "IDF-7", MapSymbols.Idf)));

        var text = TextOf(Build(map, [1]));

        Assert.Contains("Library", text);
        Assert.DoesNotContain("IDF-7", text);
        Assert.DoesNotContain("Closets", text);
    }

    [Fact]
    public void Only_the_chosen_area_is_included_and_the_legend_follows_it()
    {
        var map = Map(800, 600, Layer(1, "Closets", Roles.Tech,
            Marker(1, "Inside", MapSymbols.Idf, .2, .2),
            Marker(2, "Outside", MapSymbols.Mdf, .8, .8)));

        var text = TextOf(Build(map, [1], new MapArea(0, 0, .5, .5)));

        Assert.Contains("Inside", text);
        Assert.DoesNotContain("Outside", text);
        Assert.DoesNotContain("MDF", text);
    }

    [Fact]
    public void Labels_grow_on_a_close_up()
    {
        static double SizeOfL(byte[] pdf)
        {
            using var document = PdfDocument.Open(pdf);
            return document.GetPage(1).Letters.First(l => l.Value == "Q").PointSize;
        }
        var map = Map(800, 600, Layer(1, "Rooms", Roles.Viewer, Label(1, "Q")));

        var whole = SizeOfL(Build(map, [1]));
        var closeUp = SizeOfL(Build(map, [1], new MapArea(.4, .4, .6, .6)));

        Assert.Equal(whole * 3, closeUp, 3);
    }

    [Theory]
    [InlineData(1600, 900, 1224, 792)]
    [InlineData(900, 1600, 792, 1224)]
    public void The_page_turns_to_fit_the_map(int width, int height, double pageWidth, double pageHeight)
    {
        using var document = PdfDocument.Open(Build(Map(width, height), []));

        Assert.Equal(pageWidth, document.GetPage(1).Width);
        Assert.Equal(pageHeight, document.GetPage(1).Height);
    }

    [Theory]
    [InlineData(Roles.Viewer, false)]
    [InlineData(Roles.Tech, true)]
    public void Restricted_layers_carry_a_notice(string layerRole, bool expected)
    {
        var text = TextOf(Build(Map(800, 600, Layer(1, "Layer", layerRole, Label(1, "Library"))), [1]));

        Assert.Equal(expected, text.Contains("Contains restricted information"));
    }

    [Fact]
    public void Every_symbol_can_be_drawn_and_unusual_characters_do_not_stop_the_export()
    {
        var map = Map(800, 600, Layer(1, "Everything 日本語", Roles.Viewer,
            Marker(1, "AP", MapSymbols.IndoorAccessPoint), Marker(2, "EXT", MapSymbols.OutdoorAccessPoint), Marker(3, "577", MapSymbols.OutdoorAccessPoint577),
            Marker(4, "M", MapSymbols.Mdf), Marker(5, "I", MapSymbols.Idf), Marker(6, "Pin", MapSymbols.PhotoPin), Marker(7, "Other", "custom"),
            Marker(8, "Second star", MapSymbols.IndoorAccessPoint),
            Label(9, "Caf\u00E9 \u2013 Rm 1 \U0001F4F7 \u65E5\u672C\u8A9E\n2nd line")));

        var text = TextOf(Build(map, [1]));

        Assert.Contains("Cafe - Rm 1", text);
    }

    [Fact]
    public void The_file_name_is_safe() =>
        Assert.Equal("A-B - C-D.pdf", MapPdfExporter.FileNameFor(Map(1, 1) with { SiteName = "A/B", Name = "C:D" }));

    private async Task<(int MapId, int LayerId)> AddRestrictedLayerAsync(AsyncServiceScope scope, string mapRole = Roles.Viewer)
    {
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        var site = await factory.SaveAsync(new Site { Name = "Test School" });
        var id = (await scope.ServiceProvider.GetRequiredService<SiteMapService>()
            .CreateFromUploadAsync(new MapUpload(site.Id, null, "Site plan", SiteMapKind.SitePlan, null, "plan.pdf", TestPdf.Build(0, 800, 600), mapRole))).MapId;
        var layer = new MapLayer
        {
            SiteMapId = id,
            Name = "Closets",
            Kind = MapLayerKind.Network,
            MinimumRole = Roles.Tech,
            Features = [new MapFeature { Type = MapFeatureType.Marker, X = .5, Y = .5, Label = "IDF-SECRET", Symbol = MapSymbols.Idf }],
        };
        await using var db = await factory.CreateAsync();
        db.MapLayers.Add(layer);
        await db.SaveChangesAsync();
        return (id, layer.Id);
    }

    [Fact]
    public async Task Asking_for_a_restricted_layer_does_not_put_it_in_a_viewers_pdf()
    {
        await using var scope = _app.ScopeAs("admin");
        var (mapId, layerId) = await AddRestrictedLayerAsync(scope);
        var export = scope.ServiceProvider.GetRequiredService<MapExportService>();

        var asViewer = await export.CreatePdfAsync(mapId, new HashSet<int> { layerId }, MapArea.Whole, "today", Viewer);
        var asTech = await export.CreatePdfAsync(mapId, new HashSet<int> { layerId }, MapArea.Whole, "today", Tech);

        Assert.DoesNotContain("IDF-SECRET", TextOf(asViewer!.Content));
        Assert.DoesNotContain("Closets", TextOf(asViewer.Content));
        Assert.Contains("IDF-SECRET", TextOf(asTech!.Content));
    }

    [Fact]
    public async Task A_map_the_user_cannot_open_gives_no_pdf()
    {
        await using var scope = _app.ScopeAs("admin");
        var (mapId, layerId) = await AddRestrictedLayerAsync(scope, Roles.Tech);

        Assert.Null(await scope.ServiceProvider.GetRequiredService<MapExportService>().CreatePdfAsync(mapId, new HashSet<int> { layerId }, MapArea.Whole, "today", Viewer));
    }

    [Fact]
    public async Task Saving_a_pdf_is_in_the_audit_log()
    {
        await using var scope = _app.ScopeAs("admin");
        var (mapId, layerId) = await AddRestrictedLayerAsync(scope);

        await scope.ServiceProvider.GetRequiredService<MapExportService>().CreatePdfAsync(mapId, new HashSet<int> { layerId }, new MapArea(.25, .25, .75, .75), "today", Tech);

        var entry = await scope.ServiceProvider.GetRequiredService<PcServDbContext>().AuditEntries.SingleAsync(a => a.Action == "Map.ExportedPdf");
        Assert.Contains("Closets", entry.Summary);
    }

    public void Dispose() => _app.Dispose();
}

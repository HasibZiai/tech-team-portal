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

public class MapLayerTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Tech = UserInRole(Roles.Tech);
    private static readonly ClaimsPrincipal Viewer = UserInRole(Roles.Viewer);

    private static readonly string Navy = MapColors.All[0].Hex;
    private static readonly string Green = MapColors.All.First(c => c.Name == "Green").Hex;

    private static List<MapLegendEntryEdit> Legend(params string[] names) =>
        names.Select(n => new MapLegendEntryEdit(null, n, MapShapes.Diamond, Navy)).ToList();

    private static async Task<int> AddMapAsync(AsyncServiceScope scope, string name = "Site plan", string role = Roles.Viewer, int? siteId = null)
    {
        siteId ??= (await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "School " + Guid.NewGuid() })).Id;
        return (await scope.ServiceProvider.GetRequiredService<SiteMapService>()
            .CreateFromUploadAsync(new MapUpload(siteId.Value, null, name, SiteMapKind.SitePlan, null, name + ".pdf", TestPdf.Build(0, 800, 600), role))).MapId;
    }

    private static (MapLayerService Layers, SiteMapService Maps, MapPhotoService Photos) Services(AsyncServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<MapLayerService>(), scope.ServiceProvider.GetRequiredService<SiteMapService>(), scope.ServiceProvider.GetRequiredService<MapPhotoService>());

    [Fact]
    public async Task A_layer_keeps_its_legend_and_items_and_starts_for_techs_and_above()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, maps, _) = Services(scope);

        var layer = await layers.CreateLayerAsync(id, " Projectors ", Legend("Ceiling", "Cart"), Tech);
        var (_, item) = await layers.AddItemAsync(layer.Id, layer.Legend![1].Id, .3, .4, Tech);

        var loaded = (await maps.GetAsync(id, Tech))!.Layers.Single(l => l.Name == "Projectors");
        Assert.Equal(Roles.Tech, loaded.MinimumRole);
        Assert.Equal(["Ceiling", "Cart"], loaded.Legend!.Select(e => e.Name));
        var saved = Assert.Single(loaded.Features);
        Assert.Equal(item.Id, saved.Id);
        Assert.Equal("Cart", saved.Label);
        Assert.Equal(layer.Legend![1].Id, saved.LegendEntryId);
        Assert.Empty((await maps.GetAsync(id, Viewer))!.Layers);
    }

    [Fact]
    public async Task Opening_a_layer_to_viewers_is_a_manager_setting_that_applies()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, maps, _) = Services(scope);
        var layer = await layers.CreateLayerAsync(id, "Projectors", Legend("Ceiling"), Tech);

        await maps.SetLayerRoleAsync(layer.Id, Roles.Viewer);

        Assert.Single((await maps.GetAsync(id, Viewer))!.Layers);
    }

    [Fact]
    public async Task Viewers_cannot_make_or_change_layers()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, _, _) = Services(scope);
        var layer = await layers.CreateLayerAsync(id, "Projectors", Legend("Ceiling"), Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.CreateLayerAsync(id, "Mine", Legend("A"), Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.UpdateLayerAsync(layer.Id, "Renamed", Legend("A"), Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.DeleteLayerAsync(layer.Id, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.AddItemAsync(layer.Id, layer.Legend![0].Id, .1, .1, Viewer));
    }

    [Fact]
    public async Task A_tech_cannot_change_a_layer_above_their_role()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, maps, _) = Services(scope);
        var layer = await layers.CreateLayerAsync(id, "Managers only", Legend("A"), Tech);
        await maps.SetLayerRoleAsync(layer.Id, Roles.Manager);

        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.AddItemAsync(layer.Id, layer.Legend![0].Id, .1, .1, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.DeleteLayerAsync(layer.Id, Tech));
    }

    [Fact]
    public async Task Imported_and_photo_layers_cannot_be_changed_here()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, _, photos) = Services(scope);
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        var network = new MapLayer { SiteMapId = id, Name = "Closets", Kind = MapLayerKind.Network, MinimumRole = Roles.Tech };
        await using (var db = await factory.CreateAsync())
        {
            db.MapLayers.Add(network);
            await db.SaveChangesAsync();
        }
        var (pins, _) = await photos.CreatePinAsync(id, .5, .5, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.DeleteLayerAsync(network.Id, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.UpdateLayerAsync(pins.Id, "Renamed", Legend("A"), Tech));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Room labels")]
    [InlineData("PHOTOS AND NOTES")]
    [InlineData("taken")]
    public async Task Unusable_layer_names_are_refused(string name)
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, _, _) = Services(scope);
        await layers.CreateLayerAsync(id, "Taken", Legend("A"), Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.CreateLayerAsync(id, name, Legend("A"), Tech));
    }

    [Fact]
    public async Task Bad_legends_are_refused()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, _, _) = Services(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.CreateLayerAsync(id, "L", [], Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.CreateLayerAsync(id, "L", Legend("  "), Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.CreateLayerAsync(id, "L", Legend("Same", "same"), Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.CreateLayerAsync(id, "L", [new(null, "A", "hexagon", Navy)], Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.CreateLayerAsync(id, "L", [new(null, "A", MapShapes.Star, "red;background:url(x)")], Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.CreateLayerAsync(id, "L", Legend(Enumerable.Range(0, 21).Select(i => "E" + i).ToArray()), Tech));
    }

    [Fact]
    public async Task Editing_a_legend_changes_entries_in_place_and_items_follow()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, maps, _) = Services(scope);
        var layer = await layers.CreateLayerAsync(id, "Projectors", Legend("Ceiling", "Spare"), Tech);
        var ceiling = layer.Legend![0];
        await layers.AddItemAsync(layer.Id, ceiling.Id, .1, .1, Tech);

        await layers.UpdateLayerAsync(layer.Id, "AV", [new(ceiling.Id, "Mounted", MapShapes.Star, Green), new(null, "Portable", MapShapes.Square, Navy)], Tech);

        var loaded = (await maps.GetAsync(id, Tech))!.Layers.Single();
        Assert.Equal("AV", loaded.Name);
        Assert.Equal(["Mounted", "Portable"], loaded.Legend!.Select(e => e.Name));
        var entry = loaded.Legend!.Single(e => e.Name == "Mounted");
        Assert.Equal((MapShapes.Star, Green), (entry.Shape, entry.Color));
        Assert.Equal(entry.Id, loaded.Features.Single().LegendEntryId);
    }

    [Fact]
    public async Task A_legend_entry_in_use_cannot_be_removed()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, maps, _) = Services(scope);
        var layer = await layers.CreateLayerAsync(id, "Projectors", Legend("Ceiling", "Cart"), Tech);
        await layers.AddItemAsync(layer.Id, layer.Legend![0].Id, .1, .1, Tech);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            layers.UpdateLayerAsync(layer.Id, "Projectors", [new(layer.Legend![1].Id, "Cart", MapShapes.Diamond, Navy)], Tech));

        Assert.Contains("Ceiling", error.Message);
        Assert.Equal(2, (await maps.GetAsync(id, Tech))!.Layers.Single().Legend!.Count);
    }

    [Fact]
    public async Task Deleting_a_layer_removes_its_items_legend_and_photo_files()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, maps, photos) = Services(scope);
        var layer = await layers.CreateLayerAsync(id, "Projectors", Legend("Ceiling"), Tech);
        var (_, item) = await layers.AddItemAsync(layer.Id, layer.Legend![0].Id, .1, .1, Tech);
        using var bitmap = new SKBitmap(50, 50);
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        var photo = await photos.AddAsync(item.Id, "a.jpg", jpeg.ToArray(), null, Tech);
        var path = await photos.PhotoPathAsync(photo.Id, false, Tech);
        Assert.True(File.Exists(path));

        await layers.DeleteLayerAsync(layer.Id, Tech);

        Assert.False(File.Exists(path));
        Assert.Empty((await maps.GetAsync(id, Tech))!.Layers);
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.False(await db.MapFeatures.AnyAsync());
        Assert.False(await db.MapLegendEntries.AnyAsync());
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == nameof(MapLayer) && a.Action == "Deleted"));
    }

    [Fact]
    public async Task Items_can_be_moved_given_photos_and_stay_on_the_map()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, maps, _) = Services(scope);
        var layer = await layers.CreateLayerAsync(id, "Projectors", Legend("Ceiling"), Tech);
        var (_, outside) = await layers.AddItemAsync(layer.Id, layer.Legend![0].Id, 3, -1, Tech);
        Assert.Equal((1d, 0d), (outside.X, outside.Y));

        await maps.MoveFeatureAsync(id, outside.Id, .5, .6, Tech);

        var moved = (await maps.GetAsync(id, Tech))!.Layers.Single().Features.Single();
        Assert.Equal((.5, .6), (moved.X, moved.Y));
        await Assert.ThrowsAsync<InvalidOperationException>(() => layers.AddItemAsync(layer.Id, layer.Legend![0].Id, double.NaN, 0, Tech));
    }

    [Fact]
    public async Task A_layer_on_a_linked_map_shows_with_its_legend_but_is_changed_on_its_own_map()
    {
        await using var scope = _app.ScopeAs("admin");
        var wifi = await AddMapAsync(scope, "Wi-Fi", Roles.Tech);
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        int siteId;
        await using (var db = await factory.CreateAsync()) siteId = (await db.SiteMaps.FindAsync(wifi))!.SiteId;
        var plan = await AddMapAsync(scope, "Plan", Roles.Viewer, siteId);
        var (layers, maps, _) = Services(scope);
        var layer = await layers.CreateLayerAsync(wifi, "Projectors", Legend("Ceiling"), Tech);
        await layers.AddItemAsync(layer.Id, layer.Legend![0].Id, .4, .4, Tech);
        await using (var db = await factory.CreateAsync())
        {
            db.MapLinks.Add(new MapLink { SourceMapId = wifi, TargetMapId = plan, M00 = 1, M11 = 1, Status = MapLinkStatus.Confirmed });
            await db.SaveChangesAsync();
        }

        var onPlan = (await maps.GetAsync(plan, Tech))!.Layers.Single(l => l.Name == "Projectors");
        var onWifi = (await maps.GetAsync(wifi, Tech))!.Layers.Single(l => l.Name == "Projectors");

        Assert.Single(onPlan.Legend!);
        Assert.Single(onPlan.Features);
        Assert.False(MapLayerService.CanChange(onPlan, plan));
        Assert.True(MapLayerService.CanChange(onWifi, wifi));
    }

    [Fact]
    public async Task The_pdf_shows_custom_items_and_only_the_legend_entries_in_use()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var (layers, _, _) = Services(scope);
        var layer = await layers.CreateLayerAsync(id, "Projectors", Legend("Ceiling mount", "Never used"), Tech);
        var (_, item) = await layers.AddItemAsync(layer.Id, layer.Legend![0].Id, .5, .5, Tech);
        await scope.ServiceProvider.GetRequiredService<SiteMapService>().UpdateFeatureAsync(item.Id, "Library projector", null);
        await layers.AddItemAsync(layer.Id, layer.Legend![0].Id, .6, .6, Tech);

        var pdf = await scope.ServiceProvider.GetRequiredService<MapExportService>().CreatePdfAsync(id, new HashSet<int> { layer.Id }, MapArea.Whole, "today", Tech);

        using var document = PdfDocument.Open(pdf!.Content);
        var text = string.Concat(document.GetPage(1).Letters.Select(l => l.Value));
        Assert.Contains("Library projector", text);
        Assert.Contains("Ceiling mount", text);
        Assert.DoesNotContain("Never used", text);
    }

    [Fact]
    public void Every_shape_has_a_drawing_and_a_name()
    {
        foreach (var shape in MapShapes.All)
        {
            Assert.StartsWith("M", MapShapes.Path(shape));
            Assert.NotEqual(shape, MapShapes.DisplayName(shape));
        }
    }

    public void Dispose() => _app.Dispose();
}

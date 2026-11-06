using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;

namespace PcServ.Tests;

public class NetworkLayerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pcserv-network-tests", Guid.NewGuid().ToString("N"));
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    private NetworkLayerImportService Service(AsyncServiceScope scope) => new(
        new MapImportOptions(Path.Combine(_folder, "list.csv")),
        scope.ServiceProvider.GetRequiredService<AppDbFactory>(),
        scope.ServiceProvider.GetRequiredService<MapFileStore>());

    private void WriteLayerFile(string name, object content)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Network_Layers"));
        File.WriteAllText(Path.Combine(_folder, "Network_Layers", name), JsonSerializer.Serialize(content));
    }

    private static object SampleFile(string file) => new
    {
        file,
        features = new object[]
        {
            new { x = .25, y = .5, symbol = MapSymbols.IndoorAccessPoint, label = "TS-RM01-AP", flags = Array.Empty<string>() },
            new { x = .3, y = .5, symbol = MapSymbols.IndoorAccessPoint, label = "TS-RMO2-AP", flags = new[] { "second read: TS-RM02-AP" } },
            new { x = .4, y = .5, symbol = MapSymbols.OutdoorAccessPoint, label = (string?)null, flags = new[] { "no name found" } },
            new { x = .5, y = .5, symbol = MapSymbols.Mdf, label = "MDF", flags = Array.Empty<string>() },
        },
        strayNames = new[] { new { text = "ts-ext-01-AP", x = .6, y = .6 } },
    };

    private async Task<int> AddMapAsync(AsyncServiceScope scope, string file = "wifi.pdf")
    {
        var site = await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "Test School " + file });
        var upload = new MapUpload(site.Id, null, "Wi-Fi map", SiteMapKind.Other, null, file, TestPdf.Build(0, 800, 600), Roles.Tech);
        return (await scope.ServiceProvider.GetRequiredService<SiteMapService>().CreateFromUploadAsync(upload)).MapId;
    }

    [Fact]
    public async Task Adds_layers_and_marks_uncertain_items_for_checking()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        WriteLayerFile("TS.json", SampleFile("wifi.pdf"));

        var result = await Service(scope).ImportAsync();

        Assert.Equal(1, result.Maps);
        Assert.Equal(5, result.Items);
        Assert.Equal(3, result.ToCheck);

        var map = await scope.ServiceProvider.GetRequiredService<SiteMapService>().GetAsync(id, UserInRole(Roles.Tech));
        var aps = map!.Layers.Single(l => l.Name == NetworkLayerImportService.AccessPointsLayerName);
        var closets = map.Layers.Single(l => l.Name == NetworkLayerImportService.ClosetsLayerName);
        Assert.Equal(4, aps.Features.Count);
        Assert.Equal("MDF", Assert.Single(closets.Features).Label);
        Assert.False(aps.Features.Single(f => f.Label == "TS-RM01-AP").NeedsReview);
        Assert.Contains("second read", aps.Features.Single(f => f.Label == "TS-RMO2-AP").Notes);
        Assert.True(aps.Features.Single(f => f.Label == "TS-EXT-01-AP").NeedsReview);
        Assert.All(map.Layers, l => Assert.Equal(Roles.Tech, l.MinimumRole));
    }

    [Fact]
    public async Task Running_again_does_not_add_layers_twice()
    {
        await using var scope = _app.ScopeAs("admin");
        await AddMapAsync(scope);
        WriteLayerFile("TS.json", SampleFile("wifi.pdf"));
        var service = Service(scope);
        await service.ImportAsync();

        Assert.Equal(NetworkLayerStatus.AlreadyAdded, Assert.Single(await service.PreviewAsync()).Status);
        Assert.Equal(0, (await service.ImportAsync()).Maps);
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.Equal(2, await db.MapLayers.CountAsync());
    }

    [Fact]
    public async Task A_file_for_an_unknown_map_is_reported_and_changes_nothing()
    {
        await using var scope = _app.ScopeAs("admin");
        await AddMapAsync(scope);
        WriteLayerFile("Other.json", SampleFile("not-imported.pdf"));
        var service = Service(scope);

        Assert.Equal(NetworkLayerStatus.NoMatchingMap, Assert.Single(await service.PreviewAsync()).Status);
        Assert.Equal(0, (await service.ImportAsync()).Maps);
        Assert.False(await scope.ServiceProvider.GetRequiredService<PcServDbContext>().MapLayers.AnyAsync());
    }

    [Fact]
    public async Task Fixing_an_item_saves_the_name_and_clears_the_check()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        WriteLayerFile("TS.json", SampleFile("wifi.pdf"));
        await Service(scope).ImportAsync();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var tech = UserInRole(Roles.Tech);
        var item = (await maps.GetAsync(id, tech))!.Layers.SelectMany(l => l.Features).Single(f => f.Label == "TS-RMO2-AP");

        await maps.UpdateFeatureAsync(item.Id, " TS-RM02-AP ");

        var saved = (await maps.GetAsync(id, tech))!.Layers.SelectMany(l => l.Features).Single(f => f.Id == item.Id);
        Assert.Equal("TS-RM02-AP", saved.Label);
        Assert.False(saved.NeedsReview);

        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == nameof(MapFeature) && a.Summary.Contains("TS-RM02-AP")));

        await maps.DeleteFeatureAsync(item.Id);
        Assert.DoesNotContain((await maps.GetAsync(id, tech))!.Layers.SelectMany(l => l.Features), f => f.Id == item.Id);
    }

    [Fact]
    public void Removing_color_keeps_the_image_size()
    {
        var pdf = SampleMapService.CreatePdf();
        var normal = MapPdfReader.RenderPdf(pdf, includeAnnotations: false);
        var stripped = MapPdfReader.RenderPdfWithoutColor(pdf);

        Assert.Equal(normal.Width, stripped.Width);
        Assert.Equal(normal.Height, stripped.Height);
    }

    public void Dispose()
    {
        _app.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

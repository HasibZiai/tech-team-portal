using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;
using SkiaSharp;

#pragma warning disable CA1416

namespace PcServ.Tests;

public class MapAlignerTests
{
    // A made-up, uneven floor plan, so only one position lines it up.
    private static void DrawPlan(SKCanvas canvas, float scale, float dx, float dy)
    {
        using var paint = new SKPaint { Color = SKColors.Black, IsStroke = true, StrokeWidth = Math.Max(2, 3 * scale) };
        canvas.Translate(dx, dy);
        canvas.Scale(scale);
        canvas.DrawRect(50, 60, 300, 120, paint);
        canvas.DrawRect(400, 40, 90, 260, paint);
        canvas.DrawRect(120, 260, 210, 90, paint);
        canvas.DrawLine(120, 60, 120, 180, paint);
        canvas.DrawLine(240, 60, 240, 180, paint);
        canvas.DrawLine(400, 130, 490, 130, paint);
        canvas.DrawCircle(600, 200, 70, paint);
        canvas.DrawLine(50, 420, 650, 380, paint);
        canvas.ResetMatrix();
    }

    private static SKBitmap Plan(int width, int height, float scale, float dx, float dy)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        DrawPlan(canvas, scale, dx, dy);
        return bitmap;
    }

    [Fact]
    public void Finds_the_scale_and_position_of_the_same_building()
    {
        using var source = Plan(1600, 1200, 1.5f, 100, 150);
        using var target = Plan(1600, 1200, 1.0f, 300, 250);

        var result = MapAligner.Align(source, target);

        Assert.False(result.IsSameDrawing);
        Assert.InRange(result.Scale, .62, .72); // 1.0 / 1.5, in the aligner's shrunken drawings.

        // A corner of the building should land where it really is on the target, within about 1% of the map.
        var (x, y) = result.Transform.Apply((100 + 1.5 * 50) / 1600.0, (150 + 1.5 * 60) / 1200.0);
        Assert.InRange(x, (300 + 50) / 1600.0 - .012, (300 + 50) / 1600.0 + .012);
        Assert.InRange(y, (250 + 60) / 1200.0 - .012, (250 + 60) / 1200.0 + .012);
    }

    [Fact]
    public void The_same_drawing_is_recognized_exactly()
    {
        using var source = Plan(1600, 1200, 1.2f, 100, 100);
        using var target = Plan(1600, 1200, 1.2f, 100, 100);

        var result = MapAligner.Align(source, target);

        Assert.True(result.IsSameDrawing);
        Assert.Equal(MapTransform.Identity, result.Transform);
    }

    [Fact]
    public void Blank_images_do_not_crash()
    {
        using var source = new SKBitmap(400, 300);
        using var target = new SKBitmap(400, 300);
        using (var c = new SKCanvas(source)) c.Clear(SKColors.White);
        using (var c = new SKCanvas(target)) c.Clear(SKColors.White);

        Assert.Equal(0, MapAligner.Align(source, target).Score);
    }

    [Fact]
    public void Matching_points_give_a_transform_through_those_points()
    {
        // The target is the source at half size, moved over, on a different sized image.
        var points = new List<AlignmentPoint>
        {
            new(100 / 1000.0, 100 / 800.0, 250 / 2000.0, 150 / 1000.0),
            new(900 / 1000.0, 700 / 800.0, 650 / 2000.0, 450 / 1000.0),
        };

        var t = MapAligner.FromPoints(points, 1000, 800, 2000, 1000);

        foreach (var p in points)
        {
            var (x, y) = t.Apply(p.SourceX, p.SourceY);
            Assert.Equal(p.TargetX, x, 6);
            Assert.Equal(p.TargetY, y, 6);
        }
    }

    [Fact]
    public void Matching_points_handle_a_quarter_turn()
    {
        // On square maps, a quarter turn around the center.
        var points = new List<AlignmentPoint> { new(.2, .3, .7, .2), new(.8, .3, .7, .8) };

        var t = MapAligner.FromPoints(points, 1000, 1000, 1000, 1000);

        var (x, y) = t.Apply(.5, .5);
        Assert.Equal(.5, x, 6);
        Assert.Equal(.5, y, 6);
    }

    [Fact]
    public void Points_on_top_of_each_other_are_refused()
    {
        var points = new List<AlignmentPoint> { new(.5, .5, .5, .5), new(.5, .5, .6, .6) };
        Assert.Throws<ArgumentException>(() => MapAligner.FromPoints(points, 1000, 1000, 1000, 1000));
        Assert.Throws<ArgumentException>(() => MapAligner.FromPoints([points[0]], 1000, 1000, 1000, 1000));
    }
}

public class MapLinkTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    /// <summary>A Wi-Fi map with network layers and a plan at the same site, linked by a half-size alignment.</summary>
    private async Task<(int LinkId, int PlanId, int WifiId)> SetupAsync(AsyncServiceScope scope, MapLinkStatus status)
    {
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        var site = await factory.SaveAsync(new Site { Name = "Test School" });
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var planId = (await maps.CreateFromUploadAsync(new MapUpload(site.Id, null, "Site plan", SiteMapKind.SitePlan, null, "plan.pdf", TestPdf.Build(0, 800, 600, ("FreeText", "Rm 1", 100, 100, 10))))).MapId;
        var wifiId = (await maps.CreateFromUploadAsync(new MapUpload(site.Id, null, "Wi-Fi map", SiteMapKind.Other, null, "wifi.pdf", TestPdf.Build(0, 800, 600), Roles.Tech))).MapId;

        await using var db = await factory.CreateAsync();
        db.MapLayers.Add(new MapLayer
        {
            SiteMapId = wifiId,
            Name = "Wi-Fi access points",
            Kind = MapLayerKind.Network,
            MinimumRole = Roles.Tech,
            Features =
            [
                new MapFeature { Type = MapFeatureType.Marker, X = .4, Y = .4, Label = "TS-RM01-AP", Symbol = MapSymbols.IndoorAccessPoint },
                new MapFeature { Type = MapFeatureType.Marker, X = 3, Y = 3, Label = "TS-OFF-MAP-AP", Symbol = MapSymbols.IndoorAccessPoint },
            ],
        });
        var link = new MapLink { SourceMapId = wifiId, TargetMapId = planId, M00 = .5, M11 = .5, M02 = .1, M12 = .2, Status = status };
        db.MapLinks.Add(link);
        await db.SaveChangesAsync();
        return (link.Id, planId, wifiId);
    }

    [Fact]
    public async Task A_confirmed_alignment_shows_layers_on_the_plan_in_the_right_place()
    {
        await using var scope = _app.ScopeAs("admin");
        var (_, planId, _) = await SetupAsync(scope, MapLinkStatus.Confirmed);

        var plan = await scope.ServiceProvider.GetRequiredService<SiteMapService>().GetAsync(planId, UserInRole(Roles.Tech));

        var layer = plan!.Layers.Single(l => l.Name == "Wi-Fi access points");
        var item = Assert.Single(layer.Features); // The item that lands off the map is left out.
        Assert.Equal("TS-RM01-AP", item.Label);
        Assert.Equal(.3, item.X, 6);
        Assert.Equal(.4, item.Y, 6);
    }

    [Fact]
    public async Task An_unconfirmed_alignment_shows_nothing()
    {
        await using var scope = _app.ScopeAs("admin");
        var (_, planId, _) = await SetupAsync(scope, MapLinkStatus.Suggested);

        var plan = await scope.ServiceProvider.GetRequiredService<SiteMapService>().GetAsync(planId, UserInRole(Roles.Tech));

        Assert.DoesNotContain(plan!.Layers, l => l.Name == "Wi-Fi access points");
    }

    [Fact]
    public async Task Viewers_do_not_receive_linked_network_layers()
    {
        await using var scope = _app.ScopeAs("admin");
        var (_, planId, _) = await SetupAsync(scope, MapLinkStatus.Confirmed);

        var plan = await scope.ServiceProvider.GetRequiredService<SiteMapService>().GetAsync(planId, UserInRole(Roles.Viewer));

        Assert.Equal(SiteMapService.RoomLabelsLayerName, Assert.Single(plan!.Layers).Name);
        Assert.DoesNotContain("TS-RM01", JsonSerializer.Serialize(plan));
    }

    [Fact]
    public async Task A_layer_stays_hidden_if_its_map_is_restricted_even_when_the_layer_is_not()
    {
        await using var scope = _app.ScopeAs("admin");
        var (_, planId, wifiId) = await SetupAsync(scope, MapLinkStatus.Confirmed);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var layerId = (await maps.GetAsync(wifiId, UserInRole(Roles.Admin)))!.Layers.Single().Id;
        await maps.SetLayerRoleAsync(layerId, Roles.Viewer);

        var plan = await maps.GetAsync(planId, UserInRole(Roles.Viewer));

        Assert.DoesNotContain(plan!.Layers, l => l.Name == "Wi-Fi access points");
    }

    [Fact]
    public async Task Correcting_a_name_on_the_plan_also_corrects_it_on_the_wifi_map()
    {
        await using var scope = _app.ScopeAs("admin");
        var (_, planId, wifiId) = await SetupAsync(scope, MapLinkStatus.Confirmed);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var tech = UserInRole(Roles.Tech);
        var item = (await maps.GetAsync(planId, tech))!.Layers.Single(l => l.Name == "Wi-Fi access points").Features.Single();

        await maps.UpdateFeatureAsync(item.Id, "TS-RM99-AP");

        var onWifi = (await maps.GetAsync(wifiId, tech))!.Layers.Single().Features;
        Assert.Contains(onWifi, f => f.Label == "TS-RM99-AP");
    }

    [Fact]
    public async Task Only_pairs_at_the_same_site_are_aligned_and_never_twice()
    {
        await using var scope = _app.ScopeAs("admin");
        var (linkId, _, _) = await SetupAsync(scope, MapLinkStatus.Suggested);
        var links = scope.ServiceProvider.GetRequiredService<MapLinkService>();
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        var other = await factory.SaveAsync(new Site { Name = "Other School" });
        await scope.ServiceProvider.GetRequiredService<SiteMapService>().CreateFromUploadAsync(new MapUpload(other.Id, null, "Site plan", SiteMapKind.SitePlan, null, "other.pdf", TestPdf.Build(0, 800, 600)));

        Assert.Equal(0, await links.CountUnalignedAsync());

        await links.DeleteAsync(linkId);
        Assert.Equal(1, await links.CountUnalignedAsync());
    }

    [Fact]
    public async Task Points_replace_an_alignment_and_it_must_be_confirmed_again()
    {
        await using var scope = _app.ScopeAs("admin");
        var (linkId, planId, _) = await SetupAsync(scope, MapLinkStatus.Confirmed);
        var links = scope.ServiceProvider.GetRequiredService<MapLinkService>();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await links.SetFromPointsAsync(linkId, [new(.2, .2, .2, .2), new(.8, .8, .8, .8)]);

        Assert.Equal(MapLinkStatus.Suggested, (await links.GetAsync(linkId))!.Status);
        Assert.DoesNotContain((await maps.GetAsync(planId, UserInRole(Roles.Tech)))!.Layers, l => l.Name == "Wi-Fi access points");

        await links.ConfirmAsync(linkId);
        var item = (await maps.GetAsync(planId, UserInRole(Roles.Tech)))!.Layers.Single(l => l.Name == "Wi-Fi access points").Features.Single(f => f.Label == "TS-RM01-AP");
        Assert.Equal(.4, item.X, 6);
    }

    [Fact]
    public async Task Deleting_either_map_removes_the_alignment()
    {
        await using var scope = _app.ScopeAs("admin");
        var (linkId, _, wifiId) = await SetupAsync(scope, MapLinkStatus.Confirmed);

        await scope.ServiceProvider.GetRequiredService<SiteMapService>().DeleteAsync(wifiId);

        Assert.Null(await scope.ServiceProvider.GetRequiredService<MapLinkService>().GetAsync(linkId));
    }

    [Fact]
    public async Task The_preview_works_over_the_web_for_managers_only()
    {
        int linkId;
        await using (var scope = _app.ScopeAs("admin"))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var (name, role) in new[] { ("mgr", Roles.Manager), ("tech", Roles.Tech) })
            {
                var user = new ApplicationUser { UserName = name, DisplayName = name };
                await users.CreateAsync(user, "SomePassword123");
                await users.AddToRoleAsync(user, role);
            }
            (linkId, _, _) = await SetupAsync(scope, MapLinkStatus.Suggested);
        }

        async Task<HttpResponseMessage> FetchAs(string name)
        {
            var browser = _app.CreateBrowser();
            await TestApp.PostFormAsync(browser, "/Account/Login", "login", new() { ["Input.UserName"] = name, ["Input.Password"] = "SomePassword123" });
            return await browser.GetAsync($"/map-links/{linkId}/preview");
        }

        var manager = await FetchAs("mgr");
        Assert.Equal(HttpStatusCode.OK, manager.StatusCode);
        Assert.Equal("image/png", manager.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.Redirect, (await FetchAs("tech")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await _app.CreateBrowser().GetAsync($"/map-links/{linkId}/preview")).StatusCode);
    }

    [Fact]
    public async Task A_preview_can_be_drawn()
    {
        await using var scope = _app.ScopeAs("admin");
        var (linkId, _, _) = await SetupAsync(scope, MapLinkStatus.Suggested);

        var png = await scope.ServiceProvider.GetRequiredService<MapLinkService>().RenderPreviewAsync(linkId, new System.Security.Claims.ClaimsPrincipal());

        Assert.NotNull(png);
        Assert.NotNull(SKBitmap.Decode(png));
    }

    public void Dispose() => _app.Dispose();
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;

namespace PcServ.Tests;

public class MoveFeatureTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Tech = UserInRole(Roles.Tech);

    /// <summary>A Wi-Fi map with one access point at (.4, .4), and a plan linked at half size with an offset.</summary>
    private async Task<(int FeatureId, int LinkId, int PlanId, int WifiId)> SetupAsync(AsyncServiceScope scope, MapLinkStatus status = MapLinkStatus.Confirmed, string? notes = null)
    {
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        var site = await factory.SaveAsync(new Site { Name = "Test School" });
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var planId = (await maps.CreateFromUploadAsync(new MapUpload(site.Id, null, "Site plan", SiteMapKind.SitePlan, null, "plan.pdf", TestPdf.Build(0, 800, 600)))).MapId;
        var wifiId = (await maps.CreateFromUploadAsync(new MapUpload(site.Id, null, "Wi-Fi map", SiteMapKind.Other, null, "wifi.pdf", TestPdf.Build(0, 800, 600), Roles.Tech))).MapId;

        await using var db = await factory.CreateAsync();
        var feature = new MapFeature { Type = MapFeatureType.Marker, X = .4, Y = .4, Label = "TS-RM01-AP", Symbol = MapSymbols.IndoorAccessPoint, Notes = notes, NeedsReview = notes is not null };
        db.MapLayers.Add(new MapLayer { SiteMapId = wifiId, Name = "Wi-Fi access points", Kind = MapLayerKind.Network, MinimumRole = Roles.Tech, Features = [feature] });
        var link = new MapLink { SourceMapId = wifiId, TargetMapId = planId, M00 = .5, M11 = .5, M02 = .1, M12 = .2, Status = status };
        db.MapLinks.Add(link);
        await db.SaveChangesAsync();
        return (feature.Id, link.Id, planId, wifiId);
    }

    private static async Task<MapFeatureView> ItemOn(SiteMapService maps, int mapId) =>
        (await maps.GetAsync(mapId, Tech))!.Layers.SelectMany(l => l.Features).Single(f => f.Label == "TS-RM01-AP");

    [Fact]
    public async Task Moving_on_the_items_own_map_moves_it_and_linked_plans_follow()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, _, planId, wifiId) = await SetupAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await maps.MoveFeatureAsync(wifiId, featureId, .6, .2, Tech);

        var onWifi = await ItemOn(maps, wifiId);
        Assert.Equal(.6, onWifi.X, 6);
        Assert.Equal(.2, onWifi.Y, 6);
        var onPlan = await ItemOn(maps, planId);
        Assert.Equal(.4, onPlan.X, 6);
        Assert.Equal(.3, onPlan.Y, 6);
    }

    [Fact]
    public async Task Moving_on_a_plan_stays_on_that_plan()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, _, planId, wifiId) = await SetupAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await maps.MoveFeatureAsync(planId, featureId, .9, .8, Tech);

        var onPlan = await ItemOn(maps, planId);
        Assert.Equal(.9, onPlan.X, 6);
        Assert.Equal(.8, onPlan.Y, 6);
        var onWifi = await ItemOn(maps, wifiId);
        Assert.Equal(.4, onWifi.X, 6);
        Assert.Equal(.4, onWifi.Y, 6);
    }

    [Fact]
    public async Task Moving_again_on_a_plan_updates_the_same_position()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, _, planId, _) = await SetupAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await maps.MoveFeatureAsync(planId, featureId, .9, .8, Tech);
        await maps.MoveFeatureAsync(planId, featureId, .7, .6, Tech);

        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PcServDbContext>().MapFeaturePlacements.CountAsync());
        Assert.Equal(.7, (await ItemOn(maps, planId)).X, 6);
    }

    [Fact]
    public async Task A_hand_placed_position_survives_redoing_the_alignment()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, linkId, planId, _) = await SetupAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var links = scope.ServiceProvider.GetRequiredService<MapLinkService>();
        await maps.MoveFeatureAsync(planId, featureId, .9, .8, Tech);

        await links.SetFromPointsAsync(linkId, [new(.2, .2, .2, .2), new(.8, .8, .8, .8)]);
        await links.ConfirmAsync(linkId);

        Assert.Equal(.9, (await ItemOn(maps, planId)).X, 6);
    }

    [Fact]
    public async Task Removing_the_alignment_removes_hand_placed_positions()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, linkId, planId, _) = await SetupAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        await maps.MoveFeatureAsync(planId, featureId, .9, .8, Tech);

        await scope.ServiceProvider.GetRequiredService<MapLinkService>().DeleteAsync(linkId);

        Assert.False(await scope.ServiceProvider.GetRequiredService<PcServDbContext>().MapFeaturePlacements.AnyAsync());
    }

    [Fact]
    public async Task An_item_cannot_be_moved_through_an_unconfirmed_alignment_or_an_unrelated_map()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, _, planId, _) = await SetupAsync(scope, MapLinkStatus.Suggested);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => maps.MoveFeatureAsync(planId, featureId, .5, .5, Tech));
        Assert.False(await scope.ServiceProvider.GetRequiredService<PcServDbContext>().MapFeaturePlacements.AnyAsync());
    }

    [Theory]
    [InlineData(Roles.Viewer, false)]
    [InlineData(Roles.Tech, true)]
    [InlineData(Roles.Manager, true)]
    public async Task Only_techs_and_above_can_move_items(string role, bool allowed)
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, _, _, wifiId) = await SetupAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        if (allowed)
        {
            await maps.MoveFeatureAsync(wifiId, featureId, .5, .5, UserInRole(role));
            Assert.Equal(.5, (await ItemOn(maps, wifiId)).X, 6);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => maps.MoveFeatureAsync(wifiId, featureId, .5, .5, UserInRole(role)));
            Assert.Equal(.4, (await ItemOn(maps, wifiId)).X, 6);
        }
    }

    [Fact]
    public async Task Items_stay_on_the_map_and_bad_positions_are_refused()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, _, _, wifiId) = await SetupAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await maps.MoveFeatureAsync(wifiId, featureId, 1.5, -.5, Tech);
        var item = await ItemOn(maps, wifiId);
        Assert.Equal(1, item.X);
        Assert.Equal(0, item.Y);

        await Assert.ThrowsAsync<InvalidOperationException>(() => maps.MoveFeatureAsync(wifiId, featureId, double.NaN, .5, Tech));
    }

    [Fact]
    public async Task Placing_a_marker_that_sat_on_its_name_leaves_only_the_name_to_check()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, _, _, wifiId) = await SetupAsync(scope, notes: NetworkLayerImportService.MissingSymbolNote);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        var notes = await maps.MoveFeatureAsync(wifiId, featureId, .5, .5, Tech);

        Assert.Equal(NetworkLayerImportService.CheckNameNote, notes);
        var item = await ItemOn(maps, wifiId);
        Assert.True(item.NeedsReview);
        Assert.Equal(NetworkLayerImportService.CheckNameNote, item.Notes);
    }

    [Fact]
    public async Task Moves_are_in_the_audit_log()
    {
        await using var scope = _app.ScopeAs("admin");
        var (featureId, _, planId, wifiId) = await SetupAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await maps.MoveFeatureAsync(wifiId, featureId, .5, .5, Tech);
        await maps.MoveFeatureAsync(planId, featureId, .6, .6, Tech);

        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == nameof(MapFeature) && a.Action == "Updated"));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == nameof(MapFeaturePlacement) && a.Action == "Created"));
    }

    public void Dispose() => _app.Dispose();
}

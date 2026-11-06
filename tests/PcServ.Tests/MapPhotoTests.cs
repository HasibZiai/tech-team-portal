using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;
using SkiaSharp;

#pragma warning disable CA1416

namespace PcServ.Tests;

public class PhotoProcessingTests
{
    /// <summary>A picture that's red on its top half and blue on its bottom half, so orientation can be checked.</summary>
    private static byte[] Picture(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var red = new SKPaint { Color = SKColors.Red };
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(0, 0, width, height / 2f, red);
            canvas.DrawRect(0, height / 2f, width, height / 2f, blue);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>Adds a phone-style data block to a JPEG, holding an orientation and some marker text standing in for GPS data.</summary>
    private static byte[] WithExif(byte[] jpeg, ushort orientation, string secret)
    {
        var tiff = new List<byte>(Encoding.ASCII.GetBytes("II*\0"));
        tiff.AddRange(BitConverter.GetBytes(8));
        tiff.AddRange(BitConverter.GetBytes((ushort)1));
        tiff.AddRange(BitConverter.GetBytes((ushort)0x0112));
        tiff.AddRange(BitConverter.GetBytes((ushort)3));
        tiff.AddRange(BitConverter.GetBytes(1));
        tiff.AddRange(BitConverter.GetBytes((int)orientation));
        tiff.AddRange(BitConverter.GetBytes(0));
        tiff.AddRange(Encoding.ASCII.GetBytes(secret));

        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var length = payload.Length + 2;
        return new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload).Concat(jpeg.Skip(2)).ToArray();
    }

    private static SKColor Top(byte[] jpeg) { using var b = SKBitmap.Decode(jpeg); return b.GetPixel(b.Width / 2, 5); }

    [Fact]
    public void Removes_hidden_data_from_the_original_file()
    {
        var upload = WithExif(Picture(400, 300), 1, "GPS-SECRET-LOCATION");
        Assert.Contains("GPS-SECRET-LOCATION", Encoding.ASCII.GetString(upload));

        var result = PcServ.Web.Services.Maps.MapPhotoService.Process(upload);

        Assert.DoesNotContain("GPS-SECRET-LOCATION", Encoding.ASCII.GetString(result.Jpeg));
        Assert.DoesNotContain("GPS-SECRET-LOCATION", Encoding.ASCII.GetString(result.Thumbnail));
        Assert.DoesNotContain("Exif", Encoding.ASCII.GetString(result.Jpeg));
    }

    [Theory]
    [InlineData(1, 400, 300)]
    [InlineData(3, 400, 300)]
    [InlineData(6, 300, 400)]
    [InlineData(8, 300, 400)]
    public void Turns_sideways_phone_photos_the_right_way_up(ushort orientation, int width, int height)
    {
        var result = MapPhotoService.Process(WithExif(Picture(400, 300), orientation, ""));

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        using var bitmap = SKBitmap.Decode(result.Jpeg);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Every_orientation_produces_a_full_picture(ushort orientation)
    {
        // Each corner of the output must be picture, not empty canvas (which would save as white or black).
        var result = MapPhotoService.Process(WithExif(Picture(400, 300), orientation, ""));

        using var b = SKBitmap.Decode(result.Jpeg);
        foreach (var (x, y) in new[] { (5, 5), (b.Width - 6, 5), (5, b.Height - 6), (b.Width - 6, b.Height - 6) })
        {
            var c = b.GetPixel(x, y);
            Assert.True(c.Red > 180 && c.Blue < 90 || c.Blue > 180 && c.Red < 90, $"Corner ({x},{y}) is {c}");
        }
    }

    [Fact]
    public void Flipped_upside_down_photos_end_up_the_right_way()
    {
        // Orientation 3 means the stored picture is upside down, so blue (stored at the bottom) belongs on top.
        var result = MapPhotoService.Process(WithExif(Picture(400, 300), 3, ""));
        var top = Top(result.Jpeg);
        Assert.True(top.Blue > 180 && top.Red < 90, $"Top is {top}");
    }

    [Fact]
    public void Large_photos_are_shrunk_with_a_thumbnail()
    {
        var result = MapPhotoService.Process(Picture(4000, 3000));

        Assert.Equal(2400, result.Width);
        Assert.Equal(1800, result.Height);
        using var thumb = SKBitmap.Decode(result.Thumbnail);
        Assert.Equal(480, thumb.Width);
    }

    [Fact]
    public void Small_photos_are_not_enlarged() => Assert.Equal(400, MapPhotoService.Process(Picture(400, 300)).Width);

    [Fact]
    public void Png_is_accepted() => Assert.Equal(400, MapPhotoService.Process(Picture(400, 300, SKEncodedImageFormat.Png)).Width);

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("this is not a picture")]
    public void Things_that_are_not_pictures_are_refused(string content) =>
        Assert.Throws<MapFileException>(() => MapPhotoService.Process(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void Empty_files_are_refused() => Assert.Throws<MapFileException>(() => MapPhotoService.Process([]));
}

public class MapPhotoTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Tech = UserInRole(Roles.Tech);

    private static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(200, 100);
        using (var c = new SKCanvas(bitmap)) c.Clear(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static async Task<int> AddMapAsync(AsyncServiceScope scope, string role = Roles.Viewer)
    {
        var site = await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "Test School " + Guid.NewGuid() });
        return (await scope.ServiceProvider.GetRequiredService<SiteMapService>()
            .CreateFromUploadAsync(new MapUpload(site.Id, null, "Site plan", SiteMapKind.SitePlan, null, "plan.pdf", TestPdf.Build(0, 800, 600), role))).MapId;
    }

    [Fact]
    public async Task A_pin_creates_a_restricted_layer_once_and_shows_on_the_map()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var photos = scope.ServiceProvider.GetRequiredService<MapPhotoService>();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        var (first, _) = await photos.CreatePinAsync(id, .2, .3, Tech);
        var (second, _) = await photos.CreatePinAsync(id, .4, .5, Tech);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(Roles.Tech, first.MinimumRole);
        var layer = (await maps.GetAsync(id, Tech))!.Layers.Single(l => l.Name == MapPhotoService.PinLayerName);
        Assert.Equal(2, layer.Features.Count);
        Assert.Empty((await maps.GetAsync(id, UserInRole(Roles.Viewer)))!.Layers);
    }

    [Fact]
    public async Task Viewers_cannot_add_pins_or_photos()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var photos = scope.ServiceProvider.GetRequiredService<MapPhotoService>();
        var viewer = UserInRole(Roles.Viewer);
        var (_, pin) = await photos.CreatePinAsync(id, .2, .3, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => photos.CreatePinAsync(id, .1, .1, viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => photos.AddAsync(pin.Id, "a.jpg", Jpeg(), null, viewer));
    }

    [Fact]
    public async Task Photos_count_on_the_item_and_keep_their_caption_and_original_name()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var photos = scope.ServiceProvider.GetRequiredService<MapPhotoService>();
        var (_, pin) = await photos.CreatePinAsync(id, .2, .3, Tech);

        var added = await photos.AddAsync(pin.Id, @"C:\fakepath\..\closet.jpg", Jpeg(), " Patch panel ", Tech);

        Assert.Equal("closet.jpg", added.FileName);
        Assert.Equal("Patch panel", added.Caption);
        var item = (await scope.ServiceProvider.GetRequiredService<SiteMapService>().GetAsync(id, Tech))!.Layers.SelectMany(l => l.Features).Single();
        Assert.Equal(1, item.PhotoCount);
    }

    [Fact]
    public async Task Any_item_can_have_photos_and_notes_without_changing_its_flag_reason()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var feature = new MapFeature { Type = MapFeatureType.Marker, X = .5, Y = .5, Label = "IDF", Symbol = MapSymbols.Idf, NeedsReview = true, Notes = "flag reason" };
        await using (var db = await factory.CreateAsync())
        {
            db.MapLayers.Add(new MapLayer { SiteMapId = id, Name = "Closets", Kind = MapLayerKind.Network, MinimumRole = Roles.Tech, Features = [feature] });
            await db.SaveChangesAsync();
        }

        await scope.ServiceProvider.GetRequiredService<MapPhotoService>().AddAsync(feature.Id, "idf.jpg", Jpeg(), null, Tech);

        // Adding a photo isn't the same as checking the item.
        var item = (await maps.GetAsync(id, Tech))!.Layers.SelectMany(l => l.Features).Single();
        Assert.True(item.NeedsReview);
        Assert.Equal("flag reason", item.Notes);

        await maps.UpdateFeatureAsync(feature.Id, "IDF-1", "Behind the cabinet");
        item = (await maps.GetAsync(id, Tech))!.Layers.SelectMany(l => l.Features).Single();
        Assert.Equal("Behind the cabinet", item.Description);
        Assert.Equal("flag reason", item.Notes);
    }

    [Fact]
    public async Task Deleting_a_photo_an_item_or_a_map_removes_the_files()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var photos = scope.ServiceProvider.GetRequiredService<MapPhotoService>();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var admin = UserInRole(Roles.Admin);
        var (_, pin) = await photos.CreatePinAsync(id, .2, .3, Tech);

        var one = await photos.AddAsync(pin.Id, "1.jpg", Jpeg(), null, Tech);
        var two = await photos.AddAsync(pin.Id, "2.jpg", Jpeg(), null, Tech);
        var pathOne = await photos.PhotoPathAsync(one.Id, false, admin);
        var thumbOne = await photos.PhotoPathAsync(one.Id, true, admin);
        var pathTwo = await photos.PhotoPathAsync(two.Id, false, admin);
        Assert.True(File.Exists(pathOne));
        Assert.True(File.Exists(thumbOne));

        await photos.DeleteAsync(one.Id, Tech);
        Assert.False(File.Exists(pathOne));
        Assert.False(File.Exists(thumbOne));

        await maps.DeleteFeatureAsync(pin.Id);
        Assert.False(File.Exists(pathTwo));

        var (_, pin2) = await photos.CreatePinAsync(id, .2, .3, Tech);
        var three = await photos.AddAsync(pin2.Id, "3.jpg", Jpeg(), null, Tech);
        var pathThree = await photos.PhotoPathAsync(three.Id, false, admin);
        await maps.DeleteAsync(id);
        Assert.False(File.Exists(pathThree));
        Assert.False(await scope.ServiceProvider.GetRequiredService<PcServDbContext>().MapPhotos.AnyAsync());
    }

    [Fact]
    public async Task Photos_over_the_web_follow_the_layer_rule()
    {
        int photoId, layerId;
        await using (var scope = _app.ScopeAs("admin"))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var (name, role) in new[] { ("vee", Roles.Viewer), ("tess", Roles.Tech) })
            {
                var user = new ApplicationUser { UserName = name, DisplayName = name };
                await users.CreateAsync(user, "SomePassword123");
                await users.AddToRoleAsync(user, role);
            }
            var id = await AddMapAsync(scope);
            var photos = scope.ServiceProvider.GetRequiredService<MapPhotoService>();
            var (layer, pin) = await photos.CreatePinAsync(id, .2, .3, Tech);
            layerId = layer.Id;
            photoId = (await photos.AddAsync(pin.Id, "1.jpg", Jpeg(), null, Tech)).Id;
        }

        async Task<HttpClient> SignIn(string name)
        {
            var browser = _app.CreateBrowser();
            await TestApp.PostFormAsync(browser, "/Account/Login", "login", new() { ["Input.UserName"] = name, ["Input.Password"] = "SomePassword123" });
            return browser;
        }

        var tech = await SignIn("tess");
        var viewer = await SignIn("vee");

        var allowed = await tech.GetAsync($"/map-photos/{photoId}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("image/jpeg", allowed.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", allowed.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, (await tech.GetAsync($"/map-photos/{photoId}?thumb=true")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/map-photos/{photoId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await _app.CreateBrowser().GetAsync($"/map-photos/{photoId}")).StatusCode);

        // Opening the layer to Viewers opens its photos too.
        await using (var scope = _app.ScopeAs("admin"))
        {
            await scope.ServiceProvider.GetRequiredService<SiteMapService>().SetLayerRoleAsync(layerId, Roles.Viewer);
        }
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/map-photos/{photoId}")).StatusCode);
    }

    [Fact]
    public async Task A_bad_file_saves_nothing()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddMapAsync(scope);
        var photos = scope.ServiceProvider.GetRequiredService<MapPhotoService>();
        var (_, pin) = await photos.CreatePinAsync(id, .2, .3, Tech);

        await Assert.ThrowsAsync<MapFileException>(() => photos.AddAsync(pin.Id, "bad.jpg", Encoding.UTF8.GetBytes("not a picture"), null, Tech));

        Assert.Equal(0, await photos.CountForFeatureAsync(pin.Id));
    }

    [Fact]
    public async Task Photos_on_linked_items_are_visible_through_the_plan()
    {
        await using var scope = _app.ScopeAs("admin");
        var wifi = await AddMapAsync(scope, Roles.Tech);
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        int siteId;
        await using (var db = await factory.CreateAsync()) siteId = (await db.SiteMaps.FindAsync(wifi))!.SiteId;
        var plan = (await maps.CreateFromUploadAsync(new MapUpload(siteId, null, "Plan", SiteMapKind.SitePlan, null, "p2.pdf", TestPdf.Build(0, 800, 600)))).MapId;
        var photos = scope.ServiceProvider.GetRequiredService<MapPhotoService>();
        var (_, pin) = await photos.CreatePinAsync(wifi, .4, .4, Tech);
        await photos.AddAsync(pin.Id, "1.jpg", Jpeg(), null, Tech);
        await using (var db = await factory.CreateAsync())
        {
            db.MapLinks.Add(new MapLink { SourceMapId = wifi, TargetMapId = plan, M00 = 1, M11 = 1, Status = MapLinkStatus.Confirmed });
            await db.SaveChangesAsync();
        }

        var onPlan = (await maps.GetAsync(plan, Tech))!.Layers.SelectMany(l => l.Features).Single(f => f.Id == pin.Id);

        Assert.Equal(1, onPlan.PhotoCount);
    }

    public void Dispose() => _app.Dispose();
}

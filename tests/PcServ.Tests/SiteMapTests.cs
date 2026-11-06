using System.Globalization;
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

namespace PcServ.Tests;

public class RoleMinimumTests
{
    [Theory]
    [InlineData(Roles.Viewer, Roles.Viewer, true)]
    [InlineData(Roles.Viewer, Roles.Tech, false)]
    [InlineData(Roles.Tech, Roles.Tech, true)]
    [InlineData(Roles.Admin, Roles.Tech, true)]
    [InlineData(Roles.Manager, Roles.Admin, false)]
    [InlineData("Unknown", Roles.Viewer, false)]
    [InlineData(Roles.Admin, "Unknown", false)]
    public void Compares_roles(string held, string minimum, bool expected) =>
        Assert.Equal(expected, Roles.MeetsMinimum([held], minimum));

    [Fact]
    public void No_roles_never_pass() => Assert.False(Roles.MeetsMinimum([], Roles.Viewer));
}

public class MapPdfReaderTests
{
    [Fact]
    public void Reads_labels_positioned_as_the_page_is_displayed()
    {
        var pdf = TestPdf.Build(rotate: 0, width: 1000, height: 500,
            ("FreeText", "Rm 1", 250, 400, 10),
            ("Square", "2436", 100, 100, 10),
            ("FreeText", "RM\n2", 300, 400, 10),
            ("FreeText", "A Long School Name\n123 Some Street\nSome City, ST 00000", 800, 50, 10));

        var contents = MapPdfReader.ReadText(pdf);

        Assert.Equal(2, contents.Labels.Count);
        var label = contents.Labels[0];
        Assert.Equal("Rm 1", label.Text);
        Assert.Equal("RM\n2", contents.Labels[1].Text);
        Assert.Equal(0.25, label.X, 2);
        Assert.Equal(0.2, label.Y, 2);
        Assert.Single(contents.Notes);
        Assert.Equal(1, contents.SkippedAnnotations);
    }

    [Fact]
    public void Rejects_files_that_are_not_pdfs() =>
        Assert.Throws<MapFileException>(() => MapPdfReader.ReadText(Encoding.UTF8.GetBytes("not a pdf")));

    [Fact]
    public void Draws_the_sample_map()
    {
        var image = MapPdfReader.RenderPdf(SampleMapService.CreatePdf(), includeAnnotations: false);

        Assert.Equal(MapPdfReader.MaxImageSide, Math.Max(image.Width, image.Height));
        Assert.True(image.Png.Length > 1000);
    }

    [Fact]
    public void Self_test_passes() => Assert.Equal(0, MapRenderSelfTest.Run());
}

public class SiteMapServiceTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    [Fact]
    public async Task Viewers_never_receive_the_network_layers()
    {
        await using var scope = _app.ScopeAs("admin");
        var added = await scope.ServiceProvider.GetRequiredService<SampleMapService>().AddAsync();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        var viewer = await maps.GetAsync(added!.MapId, UserInRole(Roles.Viewer));
        var tech = await maps.GetAsync(added.MapId, UserInRole(Roles.Tech));

        Assert.All(viewer!.Layers, l => Assert.Equal(Roles.Viewer, l.MinimumRole));
        Assert.DoesNotContain(viewer.Layers, l => l.Kind == nameof(MapLayerKind.Network));
        Assert.Contains(viewer.Layers, l => l.Name == SiteMapService.RoomLabelsLayerName);
        Assert.Equal(2, tech!.Layers.Count(l => l.Kind == nameof(MapLayerKind.Network)));
    }

    [Fact]
    public async Task Changing_a_layers_role_changes_who_receives_it()
    {
        await using var scope = _app.ScopeAs("admin");
        var added = await scope.ServiceProvider.GetRequiredService<SampleMapService>().AddAsync();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var viewer = UserInRole(Roles.Viewer);

        var labels = (await maps.GetAsync(added!.MapId, viewer))!.Layers.Single(l => l.Name == SiteMapService.RoomLabelsLayerName);
        await maps.SetLayerRoleAsync(labels.Id, Roles.Manager);

        Assert.Empty((await maps.GetAsync(added.MapId, viewer))!.Layers);
    }

    [Fact]
    public async Task Sample_map_is_added_once_with_one_audit_entry()
    {
        await using var scope = _app.ScopeAs("admin");
        var sample = scope.ServiceProvider.GetRequiredService<SampleMapService>();

        Assert.NotNull(await sample.AddAsync());
        Assert.Null(await sample.AddAsync());

        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.Action == "Map.Added"));
        Assert.Equal(0, await db.AuditEntries.CountAsync(a => a.EntityType == nameof(MapFeature)));
    }

    [Fact]
    public async Task Deleting_a_map_removes_its_files_and_layers()
    {
        await using var scope = _app.ScopeAs("admin");
        var added = await scope.ServiceProvider.GetRequiredService<SampleMapService>().AddAsync();
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var path = await maps.BaseImagePathAsync(added!.MapId, UserInRole(Roles.Admin));
        Assert.True(File.Exists(path));

        await maps.DeleteAsync(added.MapId);

        Assert.False(File.Exists(path));
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.False(await db.MapLayers.AnyAsync());
        Assert.False(await db.MapFeatures.AnyAsync());
    }

    [Fact]
    public async Task Uploading_a_labeled_pdf_creates_a_labels_layer()
    {
        await using var scope = _app.ScopeAs("admin");
        var site = await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "Test School" });
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var pdf = TestPdf.Build(0, 800, 600, ("FreeText", "Rm 1", 100, 100, 10), ("FreeText", "Rm 2", 200, 100, 10));

        var result = await maps.CreateFromUploadAsync(new MapUpload(site.Id, null, "Plan", SiteMapKind.SitePlan, null, "plan.pdf", pdf));

        Assert.Equal(2, result.LabelCount);
        var view = await maps.GetAsync(result.MapId, UserInRole(Roles.Viewer));
        Assert.Equal(2, view!.Layers.Single().Features.Count);
    }

    [Theory]
    [InlineData("plan.exe")]
    [InlineData("plan.svg")]
    public async Task Other_file_types_are_rejected(string fileName)
    {
        await using var scope = _app.ScopeAs("admin");
        var site = await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "Test School" });
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await Assert.ThrowsAsync<MapFileException>(() =>
            maps.CreateFromUploadAsync(new MapUpload(site.Id, null, "Plan", SiteMapKind.SitePlan, null, fileName, [1, 2, 3])));
    }

    public void Dispose() => _app.Dispose();
}

public class MapImageEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task Map_images_require_sign_in()
    {
        int mapId;
        await using (var scope = _app.ScopeAs("admin"))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = new ApplicationUser { UserName = "viewer", DisplayName = "Viewer" };
            await users.CreateAsync(viewer, "ViewerPassword1");
            await users.AddToRoleAsync(viewer, Roles.Viewer);
            mapId = (await scope.ServiceProvider.GetRequiredService<SampleMapService>().AddAsync())!.MapId;
        }

        var anonymous = await _app.CreateBrowser().GetAsync($"/map-files/{mapId}/base");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/Account/Login", anonymous.Headers.Location?.ToString());

        var browser = _app.CreateBrowser();
        await TestApp.PostFormAsync(browser, "/Account/Login", "login", new()
        {
            ["Input.UserName"] = "viewer",
            ["Input.Password"] = "ViewerPassword1",
        });

        var signedIn = await browser.GetAsync($"/map-files/{mapId}/base");
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal("image/png", signedIn.Content.Headers.ContentType?.MediaType);
    }

    public void Dispose() => _app.Dispose();
}

/// <summary>Writes small PDFs with text annotations, since the real labeled maps aren't in the repository.</summary>
internal static class TestPdf
{
    public static byte[] Build(int rotate, double width, double height, params (string Subtype, string Text, double X, double Y, double Size)[] annotations)
    {
        static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        };

        var annotationRefs = string.Join(" ", annotations.Select((_, i) => $"{i + 4} 0 R"));
        objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(width)} {N(height)}] /Rotate {rotate} /Annots [{annotationRefs}] >>");

        foreach (var a in annotations)
        {
            var text = a.Text.Replace("\n", "\\n");
            objects.Add($"<< /Type /Annot /Subtype /{a.Subtype} /Rect [{N(a.X - a.Size)} {N(a.Y - a.Size)} {N(a.X + a.Size)} {N(a.Y + a.Size)}] /Contents ({text}) /DA (/Helv 10 Tf 0 g) >>");
        }

        var body = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(body.Length);
            body.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = body.Length;
        body.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) body.Append($"{offset:D10} 00000 n \n");
        body.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }
}

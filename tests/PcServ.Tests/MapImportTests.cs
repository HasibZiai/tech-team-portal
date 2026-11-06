using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;

namespace PcServ.Tests;

public class CsvParserTests
{
    [Fact]
    public void Reads_the_csv_excel_writes()
    {
        var lines = MapImportService.ParseCsv("﻿\"A\",B\r\n\"x, y\",\"say \"\"hi\"\"\"\r\n\r\nlast,row");

        Assert.Equal(3, lines.Count);
        Assert.Equal(["A", "B"], lines[0]);
        Assert.Equal(["x, y", "say \"hi\""], lines[1]);
        Assert.Equal(["last", "row"], lines[2]);
    }
}

public class MapImportTests : IDisposable
{
    private const string Header = "File,Site,Code,SiteType,MapName,Kind,Floor,Visibility\n";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pcserv-import-tests", Guid.NewGuid().ToString("N"));
    private readonly TestApp _app = new();

    private AsyncServiceScope Scope()
    {
        Directory.CreateDirectory(_folder);
        var scope = _app.ScopeAs("admin");
        return scope;
    }

    private MapImportService Importer(AsyncServiceScope scope, string list = "list.csv") => new(
        new MapImportOptions(Path.Combine(_folder, list)),
        scope.ServiceProvider.GetRequiredService<SiteMapService>(),
        scope.ServiceProvider.GetRequiredService<AppDbFactory>());

    private void WriteList(string body) => File.WriteAllText(Path.Combine(_folder, "list.csv"), Header + body);

    private void WritePdf(string name, params (string Subtype, string Text, double X, double Y, double Size)[] annotations)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TestPdf.Build(0, 800, 600, annotations));
    }

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    [Fact]
    public async Task Imports_sites_maps_labels_and_permissions()
    {
        await using var scope = Scope();
        WritePdf("plans/one.pdf", ("FreeText", "Rm 1", 100, 100, 10));
        WritePdf("wifi/one.pdf");
        WriteList("plans/one.pdf,Test School,TS,Elementary,Site plan,SitePlan,,Viewer\n" +
                  "wifi/one.pdf,Test School,TS,Elementary,Wi-Fi map,Other,,Tech\n");
        var importer = Importer(scope);

        var result = await importer.ImportAsync();

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.SitesCreated);
        Assert.Equal(1, result.LabelsImported);
        Assert.Empty(result.Failures);

        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        Assert.Equal(["Site plan"], (await maps.ListAsync(UserInRole(Roles.Viewer))).Select(m => m.Name));
        Assert.Equal(2, (await maps.ListAsync(UserInRole(Roles.Tech))).Count);

        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        var site = await db.Sites.SingleAsync();
        Assert.Equal("TS", site.Code);
        Assert.Equal(SiteType.Elementary, site.Type);
    }

    [Fact]
    public async Task Running_again_skips_what_is_already_imported()
    {
        await using var scope = Scope();
        WritePdf("one.pdf");
        WriteList("one.pdf,Test School,,Other,Plan,SitePlan,,Viewer\n");
        var importer = Importer(scope);
        await importer.ImportAsync();

        var preview = await importer.PreviewAsync();
        Assert.Equal(MapImportStatus.AlreadyImported, Assert.Single(preview).Status);

        var second = await importer.ImportAsync();
        Assert.Equal(0, second.Imported);
    }

    [Fact]
    public async Task Renaming_a_file_does_not_import_the_map_twice()
    {
        await using var scope = Scope();
        WritePdf("X old name.pdf");
        WriteList("X old name.pdf,Test School,,Other,Floor plan,FloorPlan,,Viewer\n");
        var importer = Importer(scope);
        await importer.ImportAsync();

        File.Move(Path.Combine(_folder, "X old name.pdf"), Path.Combine(_folder, "New name.pdf"));
        WriteList("New name.pdf,Test School,,Other,Floor plan,FloorPlan,,Viewer\n");

        Assert.Equal(MapImportStatus.AlreadyImported, Assert.Single(await importer.PreviewAsync()).Status);
        Assert.Equal(0, (await importer.ImportAsync()).Imported);

        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.Equal(1, await db.SiteMaps.CountAsync());
    }

    [Fact]
    public async Task The_same_file_name_can_belong_to_two_sites()
    {
        await using var scope = Scope();
        WritePdf("a/plan.pdf");
        WritePdf("b/plan.pdf");
        WriteList("a/plan.pdf,School A,,Other,Plan,SitePlan,,Viewer\nb/plan.pdf,School B,,Other,Plan,SitePlan,,Viewer\n");

        Assert.Equal(2, (await Importer(scope).ImportAsync()).Imported);
    }

    [Fact]
    public async Task Bad_lines_and_files_are_reported_without_stopping_the_rest()
    {
        await using var scope = Scope();
        WritePdf("good.pdf");
        File.WriteAllText(Path.Combine(_folder, "broken.pdf"), "this is not a pdf");
        WriteList("good.pdf,School,,Other,Plan,SitePlan,,Viewer\n" +
                  "broken.pdf,School,,Other,Broken,SitePlan,,Viewer\n" +
                  "missing.pdf,School,,Other,Missing,SitePlan,,Viewer\n" +
                  "good.pdf,School,,NotAType,Bad type,SitePlan,,Viewer\n" +
                  "good.pdf,School,,Other,Bad role,SitePlan,,Everyone\n");
        var importer = Importer(scope);

        var preview = await importer.PreviewAsync();
        Assert.Equal(2, preview.Count(p => p.Status == MapImportStatus.Ready));
        Assert.Equal(3, preview.Count(p => p.Status == MapImportStatus.Problem));

        var result = await importer.ImportAsync();
        Assert.Equal(1, result.Imported);
        Assert.Equal("broken.pdf", Assert.Single(result.Failures).Row.File);
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("sub/../../outside.pdf")]
    [InlineData(@"..\outside.pdf")]
    [InlineData(@"sub\..\..\outside.pdf")]
    public async Task Files_outside_the_import_folder_are_refused(string path)
    {
        await using var scope = Scope();
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(_folder)!, "outside.pdf"), TestPdf.Build(0, 100, 100));
        WriteList($"{path},School,,Other,Plan,SitePlan,,Viewer\n");

        var row = Assert.Single(await Importer(scope).PreviewAsync());

        Assert.Equal(MapImportStatus.Problem, row.Status);
        Assert.Contains("outside", row.Message);
    }

    [Theory]
    [InlineData("sub/one.pdf")]
    [InlineData(@"sub\one.pdf")]
    public async Task Files_in_subfolders_are_found_with_either_kind_of_slash(string path)
    {
        await using var scope = Scope();
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        File.WriteAllBytes(Path.Combine(_folder, "sub", "one.pdf"), TestPdf.Build(0, 100, 100));
        WriteList($"{path},School,,Other,Plan,SitePlan,,Viewer\n");

        var row = Assert.Single(await Importer(scope).PreviewAsync());

        Assert.NotEqual(MapImportStatus.Problem, row.Status);
    }

    [Theory]
    [InlineData(@"C:\fakepath\..\closet.jpg", "closet.jpg")]
    [InlineData("/home/someone/closet.jpg", "closet.jpg")]
    [InlineData(@"mixed/folders\closet.jpg", "closet.jpg")]
    [InlineData("closet.jpg", "closet.jpg")]
    public void File_names_lose_their_folders_on_any_system(string path, string expected)
    {
        Assert.Equal(expected, PcServ.Web.Services.FileNames.WithoutFolders(path));
    }

    [Fact]
    public async Task A_list_missing_columns_is_reported()
    {
        await using var scope = Scope();
        File.WriteAllText(Path.Combine(_folder, "list.csv"), "File,Site\none.pdf,School\n");

        var row = Assert.Single(await Importer(scope).PreviewAsync());

        Assert.Equal(MapImportStatus.Problem, row.Status);
        Assert.Contains("MapName", row.Message);
    }

    [Fact]
    public async Task A_missing_list_is_handled()
    {
        await using var scope = Scope();
        var importer = Importer(scope, "nothing-here.csv");

        Assert.False(importer.ListExists);
        Assert.Empty(await importer.PreviewAsync());
    }

    public void Dispose()
    {
        _app.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

public class DemoDataRemovalTests : IDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task Removes_demo_content_and_its_files_but_keeps_everything_else()
    {
        await using var scope = _app.ScopeAs("admin");
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        await using (var db = await factory.CreateAsync()) Assert.True(await DemoDataSeeder.SeedAsync(db));

        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var sample = await scope.ServiceProvider.GetRequiredService<SampleMapService>().AddAsync();
        var imagePath = await maps.BaseImagePathAsync(sample!.MapId, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Admin)], "test")));
        Assert.True(File.Exists(imagePath));

        // Things a person added, including a link inside a demo category and an edited demo link.
        var realSite = await factory.SaveAsync(new Site { Name = "Real School" });
        int demoCategoryId;
        await using (var db = await factory.CreateAsync())
        {
            demoCategoryId = await db.LinkCategories.Where(c => c.Name == "Chromebooks").Select(c => c.Id).SingleAsync();
            db.Links.Add(new LinkItem { CategoryId = demoCategoryId, Title = "My own link", Url = "https://example.org/mine" });
            var edited = await db.Links.FirstAsync(l => l.Title == "Dell Support");
            edited.Url = "https://example.org/my-dell-page";
            await db.SaveChangesAsync();
        }

        await scope.ServiceProvider.GetRequiredService<DemoDataService>().RemoveAsync();

        await using var check = await factory.CreateAsync();
        Assert.Equal([realSite.Id], await check.Sites.Select(s => s.Id).ToListAsync());
        Assert.False(await check.SiteMaps.AnyAsync());
        Assert.False(File.Exists(imagePath));

        var links = await check.Links.Select(l => l.Title).OrderBy(t => t).ToListAsync();
        Assert.Equal(["Dell Support", "My own link"], links);
        Assert.Contains(await check.LinkCategories.Select(c => c.Id).ToListAsync(), id => id == demoCategoryId);
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == "DemoData.Removed"));
    }

    [Fact]
    public async Task A_real_site_sharing_a_demo_sites_name_is_never_removed()
    {
        await using var scope = _app.ScopeAs("admin");
        var factory = scope.ServiceProvider.GetRequiredService<AppDbFactory>();
        var real = await factory.SaveAsync(new Site { Name = "District Office", Code = "DO", Type = SiteType.Office });
        var pdf = TestPdf.Build(0, 800, 600);
        await scope.ServiceProvider.GetRequiredService<SiteMapService>().CreateFromUploadAsync(
            new MapUpload(real.Id, null, "Wi-Fi map", SiteMapKind.Other, null, "wifi.pdf", pdf, Roles.Tech));
        var demo = scope.ServiceProvider.GetRequiredService<DemoDataService>();

        Assert.Equal(0, (await demo.SummarizeAsync()).Sites);
        await demo.RemoveAsync();

        await using var check = await factory.CreateAsync();
        Assert.True(await check.Sites.AnyAsync(s => s.Id == real.Id));
        Assert.Equal(1, await check.SiteMaps.CountAsync());
    }

    public void Dispose() => _app.Dispose();
}

public class MapPermissionTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, role)], "test"));

    private async Task<int> AddTechOnlyMapAsync(AsyncServiceScope scope)
    {
        var site = await scope.ServiceProvider.GetRequiredService<AppDbFactory>().SaveAsync(new Site { Name = "Test School" });
        var upload = new MapUpload(site.Id, null, "Wi-Fi map", SiteMapKind.Other, null, "wifi.pdf", TestPdf.Build(0, 800, 600), Roles.Tech);
        return (await scope.ServiceProvider.GetRequiredService<SiteMapService>().CreateFromUploadAsync(upload)).MapId;
    }

    [Fact]
    public async Task A_restricted_map_is_hidden_from_lower_roles_everywhere()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddTechOnlyMapAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();
        var viewer = UserInRole(Roles.Viewer);
        var tech = UserInRole(Roles.Tech);

        Assert.Empty(await maps.ListAsync(viewer));
        Assert.Null(await maps.GetAsync(id, viewer));
        Assert.Null(await maps.BaseImagePathAsync(id, viewer));

        Assert.Single(await maps.ListAsync(tech));
        Assert.NotNull(await maps.GetAsync(id, tech));
        Assert.NotNull(await maps.BaseImagePathAsync(id, tech));
    }

    [Fact]
    public async Task A_viewer_cannot_fetch_a_restricted_image_directly()
    {
        int id;
        await using (var scope = _app.ScopeAs("admin"))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = new ApplicationUser { UserName = "viewer", DisplayName = "Viewer" };
            await users.CreateAsync(viewer, "ViewerPassword1");
            await users.AddToRoleAsync(viewer, Roles.Viewer);
            id = await AddTechOnlyMapAsync(scope);
        }

        var browser = _app.CreateBrowser();
        await TestApp.PostFormAsync(browser, "/Account/Login", "login", new()
        {
            ["Input.UserName"] = "viewer",
            ["Input.Password"] = "ViewerPassword1",
        });

        var response = await browser.GetAsync($"/map-files/{id}/base");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Changing_a_maps_role_takes_effect()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await AddTechOnlyMapAsync(scope);
        var maps = scope.ServiceProvider.GetRequiredService<SiteMapService>();

        await maps.SetMapRoleAsync(id, Roles.Viewer);

        Assert.NotNull(await maps.GetAsync(id, UserInRole(Roles.Viewer)));
    }

    public void Dispose() => _app.Dispose();
}

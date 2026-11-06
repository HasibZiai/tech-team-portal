using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services;
using SkiaSharp;

#pragma warning disable CA1416

namespace PcServ.Tests;

public class WikiHtmlTests
{
    [Theory]
    [InlineData("<p>Hi<script>alert(1)</script></p>", "<p>Hi</p>")]
    [InlineData("<p onclick=\"alert(1)\">Hi</p>", "<p>Hi</p>")]
    [InlineData("<p style=\"color:red\">Hi</p>", "<p>Hi</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">Hi</a>", "<a>Hi</a>")]
    [InlineData("<iframe src=\"https://example.com\"></iframe><p>Hi</p>", "<p>Hi</p>")]
    [InlineData("<style>p{color:red}</style><p>Hi</p>", "<p>Hi</p>")]
    [InlineData("<h1>Top</h1>", "Top")]
    [InlineData("<font color=\"red\">Hi</font>", "Hi")]
    public void Unsafe_or_unwanted_content_is_removed(string html, string expected) => Assert.Equal(expected, WikiHtml.Clean(html));

    [Theory]
    [InlineData("<img src=\"https://example.com/track.gif\">")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">")]
    [InlineData("<img src=\"wiki-images/../../secret\">")]
    [InlineData("<img src=\"javascript:alert(1)\">")]
    public void Images_from_elsewhere_are_removed(string html) => Assert.Equal("", WikiHtml.Clean(html));

    [Fact]
    public void Formatting_the_editor_makes_survives()
    {
        var key = new string('a', 32);
        var html = $"<h2>A</h2><h3>B</h3><p><strong>b</strong> <em>i</em> <u>u</u> <code>c</code></p><ul><li><p>x</p></li></ul><ol start=\"3\"><li><p>y</p></li></ol><pre><code>z</code></pre><hr><table><tbody><tr><th colspan=\"2\" rowspan=\"1\"><p>h</p></th></tr><tr><td><p>d</p></td></tr></tbody></table><div data-callout=\"warning\"><p>w</p></div><img src=\"wiki-images/{key}\" alt=\"pic\">";

        var clean = WikiHtml.Clean(html);

        foreach (var part in new[] { "<h2>A</h2>", "<h3>B</h3>", "<strong>b</strong>", "<em>i</em>", "<u>u</u>", "<code>c</code>", "<ul>", "start=\"3\"", "<pre><code>z</code></pre>", "<hr>", "colspan=\"2\"", "data-callout=\"warning\"", $"src=\"wiki-images/{key}\"", "alt=\"pic\"" })
        {
            Assert.Contains(part, clean);
        }
    }

    [Fact]
    public void Web_links_open_safely_in_a_new_tab_and_other_links_stay_put()
    {
        var clean = WikiHtml.Clean("<a href=\"https://example.com\" target=\"_top\">a</a><a href=\"wiki/5\">b</a>");

        Assert.Contains("target=\"_blank\"", clean);
        Assert.Contains("rel=\"noopener noreferrer\"", clean);
        Assert.Contains("<a href=\"wiki/5\">b</a>", clean);
    }

    [Fact]
    public void Empty_header_cells_become_ordinary_cells()
    {
        var clean = WikiHtml.Clean("<table><tbody><tr><th colspan=\"2\"><p>Name</p></th><th colspan=\"2\"><p></p></th></tr></tbody></table>");

        Assert.Contains("<th colspan=\"2\"><p>Name</p></th>", clean);
        Assert.Contains("<td colspan=\"2\"><p></p></td>", clean);
    }

    [Theory]
    [InlineData("<svg><script>alert(1)</script></svg><p>Hi</p>")]
    [InlineData("<noscript><p>hidden</p></noscript><p>Hi</p>")]
    public void Content_inside_dangerous_tags_goes_too(string html) => Assert.Equal("<p>Hi</p>", WikiHtml.Clean(html));

    [Fact]
    public void Unknown_box_types_lose_the_marker()
    {
        Assert.Equal("<div><p>x</p></div>", WikiHtml.Clean("<div data-callout=\"evil\"><p>x</p></div>"));
    }

    [Fact]
    public void Headings_get_unique_addresses_and_a_list()
    {
        var (html, headings) = WikiHtml.WithHeadings("<h2>Set up</h2><p>x</p><h3>Set up</h3><h2>  </h2><h2>Done &amp; dusted!</h2>");

        Assert.Equal(["h-set-up", "h-set-up-2", "h-done-dusted"], headings.Select(h => h.Id));
        Assert.Equal([2, 3, 2], headings.Select(h => h.Level));
        Assert.Equal("Done & dusted!", headings[2].Text);
        Assert.Contains("id=\"h-set-up-2\"", html);
    }

    [Fact]
    public void Text_is_extracted_for_searching()
    {
        Assert.Equal("One & two three", WikiHtml.TextOf("<p>One &amp; <strong>two</strong></p><p>three</p>"));
        Assert.False(WikiHtml.HasContent("<p> </p>"));
        Assert.True(WikiHtml.HasContent("<img src=\"x\">"));
    }
}

public class WikiTests : IDisposable
{
    private readonly TestApp _app = new();

    private static ClaimsPrincipal UserInRole(string role, string name = "pat") => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Tech = UserInRole(Roles.Tech);
    private static readonly ClaimsPrincipal Manager = UserInRole(Roles.Manager, "morgan");
    private static readonly ClaimsPrincipal Viewer = UserInRole(Roles.Viewer, "vee");

    private static WikiService Wiki(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<WikiService>();

    private static async Task<int> SectionAsync(AsyncServiceScope scope) => (await Wiki(scope).TreeAsync(Tech)).First().Id;

    private static async Task<int> PublishedPageAsync(AsyncServiceScope scope, string title = "How to", string html = "<p>Body</p>", string role = Roles.Viewer)
    {
        var id = await Wiki(scope).CreatePageAsync(await SectionAsync(scope), title, role, Tech);
        Assert.True((await Wiki(scope).PublishAsync(id, title, html, 0, false, Tech)).Published);
        return id;
    }

    [Fact]
    public async Task There_is_a_section_to_start_with()
    {
        await using var scope = _app.ScopeAs("admin");

        Assert.Equal("General", Assert.Single(await Wiki(scope).TreeAsync(Viewer)).Name);
    }

    [Fact]
    public async Task A_new_page_stays_hidden_from_readers_until_published()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Wiki(scope).CreatePageAsync(await SectionAsync(scope), " Printers ", Roles.Viewer, Tech);

        Assert.Null(await Wiki(scope).GetPageAsync(id, Viewer));
        Assert.Empty((await Wiki(scope).TreeAsync(Viewer)).SelectMany(s => s.Pages));
        Assert.Equal("Printers", (await Wiki(scope).GetPageAsync(id, Tech))!.Title);

        await Wiki(scope).PublishAsync(id, "Printers", "<h2>Setup</h2><p>Plug it in</p>", 0, false, Tech);

        var page = (await Wiki(scope).GetPageAsync(id, Viewer))!;
        Assert.Equal(1, page.CurrentVersion);
        Assert.Equal("pat", page.PublishedBy);
        Assert.Equal("Setup", Assert.Single(page.Headings).Text);
        Assert.Contains("id=\"h-setup\"", page.Html);
    }

    [Fact]
    public async Task Tech_only_pages_are_hidden_everywhere_from_viewers()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await PublishedPageAsync(scope, "Closet codes", "<p>secretword</p>", Roles.Tech);

        Assert.Null(await Wiki(scope).GetPageAsync(id, Viewer));
        Assert.Empty((await Wiki(scope).TreeAsync(Viewer)).SelectMany(s => s.Pages));
        Assert.Empty(await Wiki(scope).SearchAsync("secretword", Viewer));
        Assert.Empty(await Wiki(scope).RecentAsync(Viewer));
        Assert.Single(await Wiki(scope).SearchAsync("secretword", Tech));
    }

    [Fact]
    public async Task Viewers_cannot_change_anything()
    {
        await using var scope = _app.ScopeAs("admin");
        var section = await SectionAsync(scope);
        var id = await PublishedPageAsync(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).CreatePageAsync(section, "T", Roles.Viewer, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).SaveDraftAsync(id, "T", "<p>x</p>", 1, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).PublishAsync(id, "T", "<p>x</p>", 1, true, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).HistoryAsync(id, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).RestoreAsync(id, 1, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).UpdateSettingsAsync(id, section, Roles.Viewer, Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).ListImagesAsync(Viewer));
    }

    [Fact]
    public async Task Only_managers_arrange_sections_and_delete_pages()
    {
        await using var scope = _app.ScopeAs("admin");
        var section = await SectionAsync(scope);
        var id = await PublishedPageAsync(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).SaveSectionAsync(0, "New", Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).MoveSectionAsync(section, 1, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).DeleteSectionAsync(section, Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).DeletePageAsync(id, Tech));

        await Wiki(scope).DeletePageAsync(id, Manager);
        Assert.Null(await Wiki(scope).GetPageAsync(id, Manager));
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        Assert.False(await db.WikiPageVersions.AnyAsync());
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "Wiki.PageDeleted"));
    }

    [Fact]
    public async Task A_section_with_pages_cannot_be_deleted_and_names_stay_unique()
    {
        await using var scope = _app.ScopeAs("admin");
        var section = await SectionAsync(scope);
        await PublishedPageAsync(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).DeleteSectionAsync(section, Manager));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).SaveSectionAsync(0, "general", Manager));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).SaveSectionAsync(0, "  ", Manager));

        await Wiki(scope).SaveSectionAsync(0, "Empty", Manager);
        var empty = (await Wiki(scope).TreeAsync(Manager)).Single(s => s.Name == "Empty").Id;
        await Wiki(scope).MoveSectionAsync(empty, -1, Manager);
        Assert.Equal("Empty", (await Wiki(scope).TreeAsync(Manager)).First().Name);
        await Wiki(scope).DeleteSectionAsync(empty, Manager);
        Assert.Single(await Wiki(scope).TreeAsync(Manager));
    }

    [Fact]
    public async Task Drafts_are_private_to_their_author_and_cleaned()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await PublishedPageAsync(scope, html: "<p>Published</p>");
        var other = UserInRole(Roles.Tech, "sam");

        await Wiki(scope).SaveDraftAsync(id, "Draft title", "<p>Mine<script>x</script></p>", 1, Tech);

        var draft = (await Wiki(scope).GetDraftAsync(id, Tech))!;
        Assert.Equal(("Draft title", "<p>Mine</p>", 1), (draft.Title, draft.Html, draft.BaseVersion));
        Assert.Null(await Wiki(scope).GetDraftAsync(id, other));
        Assert.Contains("Published", (await Wiki(scope).GetPageAsync(id, Viewer))!.Html);

        await Wiki(scope).DiscardDraftAsync(id, Tech);
        Assert.Null(await Wiki(scope).GetDraftAsync(id, Tech));
    }

    [Fact]
    public async Task Saving_a_draft_again_updates_it_and_keeps_where_it_started()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await PublishedPageAsync(scope);

        await Wiki(scope).SaveDraftAsync(id, "One", "<p>1</p>", 1, Tech);
        await Wiki(scope).SaveDraftAsync(id, "Two", "<p>2</p>", 7, Tech);

        var draft = (await Wiki(scope).GetDraftAsync(id, Tech))!;
        Assert.Equal(("Two", 1), (draft.Title, draft.BaseVersion));
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PcServDbContext>().WikiDrafts.CountAsync());
    }

    [Fact]
    public async Task Publishing_clears_the_draft_and_keeps_every_version()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await PublishedPageAsync(scope, html: "<p>First</p>");
        await Wiki(scope).SaveDraftAsync(id, "New", "<p>Second</p>", 1, Tech);

        var result = await Wiki(scope).PublishAsync(id, "New", "<p>Second</p>", 1, false, Tech);

        Assert.Equal((true, 2), (result.Published, result.Version));
        Assert.Null(await Wiki(scope).GetDraftAsync(id, Tech));
        Assert.Equal([2, 1], (await Wiki(scope).HistoryAsync(id, Tech)).Select(v => v.Number));
    }

    [Fact]
    public async Task A_second_person_cannot_overwrite_without_confirming()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await PublishedPageAsync(scope, html: "<p>Original</p>");
        var sam = UserInRole(Roles.Tech, "sam");

        Assert.True((await Wiki(scope).PublishAsync(id, "T", "<p>Sam's</p>", 1, false, sam)).Published);

        var refused = await Wiki(scope).PublishAsync(id, "T", "<p>Pat's</p>", 1, false, Tech);
        Assert.False(refused.Published);
        Assert.Equal("sam", refused.NewerBy);
        Assert.Contains("Sam", (await Wiki(scope).GetPageAsync(id, Viewer))!.Html);

        Assert.True((await Wiki(scope).PublishAsync(id, "T", "<p>Pat's</p>", 1, true, Tech)).Published);
        var history = await Wiki(scope).HistoryAsync(id, Tech);
        Assert.Equal(3, history.Count);
        Assert.Contains("Sam", history.Single(v => v.Number == 2).Html);
    }

    [Fact]
    public async Task Restoring_adds_a_new_version_instead_of_losing_later_ones()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await PublishedPageAsync(scope, "Old title", "<p>Old</p>");
        await Wiki(scope).PublishAsync(id, "New title", "<p>New words</p>", 1, false, Tech);

        await Wiki(scope).RestoreAsync(id, 1, Tech);

        var page = (await Wiki(scope).GetPageAsync(id, Viewer))!;
        Assert.Equal(("Old title", 3), (page.Title, page.CurrentVersion));
        var history = await Wiki(scope).HistoryAsync(id, Tech);
        Assert.Equal("Restored version 1", history[0].Note);
        Assert.Equal(3, history.Count);
        Assert.Empty(await Wiki(scope).SearchAsync("words", Viewer));
    }

    [Theory]
    [InlineData("", "<p>x</p>")]
    [InlineData("Title", "")]
    [InlineData("Title", "<p>  </p>")]
    [InlineData("Title", "<script>only</script>")]
    public async Task Empty_titles_and_pages_are_refused(string title, string html)
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await Wiki(scope).CreatePageAsync(await SectionAsync(scope), "T", Roles.Viewer, Tech);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).PublishAsync(id, title, html, 0, false, Tech));
    }

    [Fact]
    public async Task Only_everyone_or_techs_are_valid_readers()
    {
        await using var scope = _app.ScopeAs("admin");
        var section = await SectionAsync(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).CreatePageAsync(section, "T", Roles.Admin, Manager));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).CreatePageAsync(section, "T", "Nobody", Manager));
    }

    [Fact]
    public async Task Search_matches_all_words_puts_titles_first_and_ignores_drafts()
    {
        await using var scope = _app.ScopeAs("admin");
        await PublishedPageAsync(scope, "Zebra printers", "<p>Guide to labels</p>");
        await PublishedPageAsync(scope, "Another page", "<p>Printers are in the zebra closet, among other things</p>");
        var draftOnly = await PublishedPageAsync(scope, "Third", "<p>Unrelated</p>");
        await Wiki(scope).SaveDraftAsync(draftOnly, "Third", "<p>zebra printers</p>", 1, Tech);

        var results = await Wiki(scope).SearchAsync(" ZEBRA printers ", Viewer);

        Assert.Equal(["Zebra printers", "Another page"], results.Select(r => r.Title));
        Assert.Contains("zebra closet", results[1].Snippet);
        Assert.Empty(await Wiki(scope).SearchAsync("zebra guide nothing", Viewer));
        Assert.Empty(await Wiki(scope).SearchAsync("   ", Viewer));
    }

    [Fact]
    public async Task Previous_and_next_follow_the_order_and_skip_what_a_reader_cannot_see()
    {
        await using var scope = _app.ScopeAs("admin");
        var first = await PublishedPageAsync(scope, "First");
        await PublishedPageAsync(scope, "Hidden", role: Roles.Tech);
        var last = await PublishedPageAsync(scope, "Last");

        var page = (await Wiki(scope).GetPageAsync(first, Viewer))!;
        Assert.Null(page.Previous);
        Assert.Equal("Last", page.Next!.Title);
        Assert.Equal("Hidden", (await Wiki(scope).GetPageAsync(first, Tech))!.Next!.Title);

        await Wiki(scope).MovePageAsync(last, -1, Tech);
        await Wiki(scope).MovePageAsync(last, -1, Tech);
        await Wiki(scope).MovePageAsync(last, -1, Tech);
        Assert.Equal("First", (await Wiki(scope).GetPageAsync(last, Viewer))!.Next!.Title);
        Assert.Null((await Wiki(scope).GetPageAsync(last, Viewer))!.Previous);
    }

    [Fact]
    public async Task Settings_move_a_page_and_change_who_can_read_it()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await PublishedPageAsync(scope);
        await Wiki(scope).SaveSectionAsync(0, "Other", Manager);
        var other = (await Wiki(scope).TreeAsync(Tech)).Single(s => s.Name == "Other").Id;

        await Wiki(scope).UpdateSettingsAsync(id, other, Roles.Tech, Tech);

        Assert.Null(await Wiki(scope).GetPageAsync(id, Viewer));
        Assert.Equal("Other", (await Wiki(scope).GetPageAsync(id, Tech))!.SectionName);
    }

    [Fact]
    public async Task The_audit_log_records_publishes_and_not_every_keystroke()
    {
        await using var scope = _app.ScopeAs("admin");
        var id = await PublishedPageAsync(scope, "Audited");
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        var before = await db.AuditEntries.CountAsync();

        await Wiki(scope).SaveDraftAsync(id, "T", "<p>1</p>", 1, Tech);
        await Wiki(scope).SaveDraftAsync(id, "T", "<p>2</p>", 1, Tech);
        Assert.Equal(before, await db.AuditEntries.CountAsync());

        var entry = await db.AuditEntries.FirstAsync(a => a.Action == "Wiki.Published");
        Assert.Contains("Audited", entry.Summary);
        Assert.DoesNotContain("<p>", entry.Summary);
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(64, 48);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task Uploaded_images_join_the_library_and_can_be_used_on_a_page()
    {
        await using var scope = _app.ScopeAs("admin");
        var image = await Wiki(scope).UploadImageAsync(@"C:\fake\screen.png", Png(), Tech);

        Assert.Equal("screen.png", image.FileName);
        Assert.Matches("^wiki-images/[0-9a-f]{32}$", image.Url);
        Assert.Single(await Wiki(scope).ListImagesAsync(Tech));

        var id = await PublishedPageAsync(scope, html: $"<img src=\"{image.Url}\" alt=\"Screen\">");
        Assert.Contains(image.Url, (await Wiki(scope).GetPageAsync(id, Viewer))!.Html);

        await Assert.ThrowsAsync<PcServ.Web.Services.Maps.MapFileException>(() => Wiki(scope).UploadImageAsync("x.png", "not a picture"u8.ToArray(), Tech));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wiki(scope).UploadImageAsync("x.png", Png(), Viewer));
    }

    [Fact]
    public async Task Images_need_signing_in_and_a_real_key()
    {
        string url;
        await using (var scope = _app.ScopeAs("admin"))
        {
            url = (await Wiki(scope).UploadImageAsync("a.png", Png(), Tech)).Url;
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = new ApplicationUser { UserName = "vee", DisplayName = "vee" };
            await users.CreateAsync(viewer, "SomePassword123");
            await users.AddToRoleAsync(viewer, Roles.Viewer);
        }

        Assert.Equal(HttpStatusCode.Redirect, (await _app.CreateBrowser().GetAsync("/" + url)).StatusCode);

        var browser = _app.CreateBrowser();
        await TestApp.PostFormAsync(browser, "/Account/Login", "login", new() { ["Input.UserName"] = "vee", ["Input.Password"] = "SomePassword123" });
        var image = await browser.GetAsync("/" + url);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/{url}?thumb=true")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/wiki-images/" + new string('0', 32))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/wiki-images/..%2F..%2Fpcserv")).StatusCode);
    }

    public void Dispose() => _app.Dispose();
}

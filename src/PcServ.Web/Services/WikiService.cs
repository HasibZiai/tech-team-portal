using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services.Maps;

namespace PcServ.Web.Services;

public sealed record WikiPageLink(int Id, string Title, int SectionId, string MinimumRole, bool IsPublished);

public sealed record WikiSectionView(int Id, string Name, IReadOnlyList<WikiPageLink> Pages);

public sealed record WikiPageView(
    int Id, int SectionId, string SectionName, string Title, string Html, IReadOnlyList<WikiHeading> Headings, string MinimumRole,
    int CurrentVersion, DateTime? PublishedAt, string? PublishedBy, WikiPageLink? Previous, WikiPageLink? Next);

public sealed record WikiDraftView(string Title, string Html, int BaseVersion, DateTime SavedAt);

public sealed record WikiVersionView(int Number, string Title, string Html, string? Note, string By, DateTime At);

public sealed record WikiPublishResult(bool Published, int Version, string? NewerBy);

public sealed record WikiSearchResult(int PageId, string Title, string SectionName, string Snippet);

public sealed record WikiRecentPage(int Id, string Title, string SectionName, DateTime At, string? By);

public sealed record WikiImageView(string Key, string FileName, string Url);

/// <summary>
/// The wiki. Everyone signed in can read what their role allows, Techs and above write, and Managers and above
/// arrange sections and delete pages. Checked on every call, not just on the pages.
/// </summary>
public sealed class WikiService(AppDbFactory dbFactory, WikiImageStore images)
{
    public const int MaxHtmlLength = 2_000_000;

    private static readonly string[] PageRoles = [Roles.Viewer, Roles.Tech];

    // ---- Reading ----

    public async Task<List<WikiSectionView>> TreeAsync(ClaimsPrincipal user)
    {
        await using var db = await dbFactory.CreateAsync();
        return await TreeAsync(db, user);
    }

    private static async Task<List<WikiSectionView>> TreeAsync(PcServDbContext db, ClaimsPrincipal user)
    {
        var sections = await db.WikiSections.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
        var pages = await db.WikiPages.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .Select(p => new WikiPageLink(p.Id, p.Title, p.SectionId, p.MinimumRole, p.CurrentVersion > 0)).ToListAsync();
        var visible = pages.Where(p => CanSee(user, p.MinimumRole, p.IsPublished)).ToList();
        return sections.Select(s => new WikiSectionView(s.Id, s.Name, visible.Where(p => p.SectionId == s.Id).ToList())).ToList();
    }

    /// <summary>A page, or null if it doesn't exist or this user can't see it.</summary>
    public async Task<WikiPageView?> GetPageAsync(int id, ClaimsPrincipal user)
    {
        await using var db = await dbFactory.CreateAsync();
        var page = await db.WikiPages.AsNoTracking().Include(p => p.Section).FirstOrDefaultAsync(p => p.Id == id);
        if (page is null || !CanSee(user, page.MinimumRole, page.CurrentVersion > 0)) return null;

        var order = (await TreeAsync(db, user)).SelectMany(s => s.Pages).ToList();
        var position = order.FindIndex(p => p.Id == id);
        var (html, headings) = WikiHtml.WithHeadings(page.Html);
        return new WikiPageView(page.Id, page.SectionId, page.Section!.Name, page.Title, html, headings, page.MinimumRole, page.CurrentVersion, page.PublishedAt, page.PublishedBy,
            position > 0 ? order[position - 1] : null, position >= 0 && position < order.Count - 1 ? order[position + 1] : null);
    }

    public async Task<List<WikiSearchResult>> SearchAsync(string? query, ClaimsPrincipal user)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];

        await using var db = await dbFactory.CreateAsync();
        var pages = await db.WikiPages.AsNoTracking().Where(p => p.CurrentVersion > 0)
            .Select(p => new { p.Id, p.Title, p.SearchText, p.MinimumRole, Section = p.Section!.Name }).ToListAsync();

        return pages.Where(p => Holds(user, p.MinimumRole))
            .Where(p => terms.All(t => p.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || p.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => terms.Count(t => p.Title.Contains(t, StringComparison.OrdinalIgnoreCase))).ThenBy(p => p.Title)
            .Take(50)
            .Select(p => new WikiSearchResult(p.Id, p.Title, p.Section, Snippet(p.SearchText, terms)))
            .ToList();
    }

    public async Task<List<WikiRecentPage>> RecentAsync(ClaimsPrincipal user, int count = 8)
    {
        await using var db = await dbFactory.CreateAsync();
        var pages = await db.WikiPages.AsNoTracking().Where(p => p.CurrentVersion > 0).OrderByDescending(p => p.PublishedAt)
            .Select(p => new { p.Id, p.Title, p.MinimumRole, Section = p.Section!.Name, p.PublishedAt, p.PublishedBy }).ToListAsync();
        return pages.Where(p => Holds(user, p.MinimumRole)).Take(count).Select(p => new WikiRecentPage(p.Id, p.Title, p.Section, p.PublishedAt!.Value, p.PublishedBy)).ToList();
    }

    // ---- Writing ----

    public async Task<int> CreatePageAsync(int sectionId, string? title, string? minimumRole, ClaimsPrincipal user)
    {
        RequireTech(user);
        var cleanTitle = CheckTitle(title);
        CheckRole(minimumRole);
        await using var db = await dbFactory.CreateAsync();
        if (!await db.WikiSections.AnyAsync(s => s.Id == sectionId)) throw new InvalidOperationException("Choose a section.");
        var sort = await db.WikiPages.Where(p => p.SectionId == sectionId).Select(p => (int?)p.SortOrder).MaxAsync() ?? -1;
        var page = new WikiPage { SectionId = sectionId, Title = cleanTitle, MinimumRole = minimumRole!, SortOrder = sort + 1 };
        db.WikiPages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    public async Task<WikiDraftView?> GetDraftAsync(int pageId, ClaimsPrincipal user)
    {
        RequireTech(user);
        var name = NameOf(user);
        await using var db = await dbFactory.CreateAsync();
        var draft = await db.WikiDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.PageId == pageId && d.UserName == name);
        return draft is null ? null : new WikiDraftView(draft.Title, draft.Html, draft.BaseVersion, draft.UpdatedAt ?? draft.CreatedAt);
    }

    /// <summary>Saves someone's work as they type. It's not in the audit log, which only records what gets published.</summary>
    public async Task SaveDraftAsync(int pageId, string? title, string? html, int baseVersion, ClaimsPrincipal user)
    {
        RequireTech(user);
        CheckLength(html);
        var name = NameOf(user);
        await using var db = await dbFactory.CreateAsync();
        db.SuppressEntityAudit = true;
        await LoadEditableAsync(db, pageId, user);

        var draft = await db.WikiDrafts.FirstOrDefaultAsync(d => d.PageId == pageId && d.UserName == name);
        if (draft is null)
        {
            draft = new WikiDraft { PageId = pageId, UserName = name, BaseVersion = baseVersion };
            db.WikiDrafts.Add(draft);
        }
        draft.Title = Truncate(title?.Trim() ?? "", 150);
        draft.Html = WikiHtml.Clean(html);
        await db.SaveChangesAsync();
    }

    public async Task DiscardDraftAsync(int pageId, ClaimsPrincipal user)
    {
        RequireTech(user);
        var name = NameOf(user);
        await using var db = await dbFactory.CreateAsync();
        db.SuppressEntityAudit = true;
        var draft = await db.WikiDrafts.FirstOrDefaultAsync(d => d.PageId == pageId && d.UserName == name);
        if (draft is null) return;
        db.WikiDrafts.Remove(draft);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Publishes a new version. If someone else published since this was started, nothing happens unless the person
    /// confirms, and even then the other version stays in the history.
    /// </summary>
    public async Task<WikiPublishResult> PublishAsync(int pageId, string? title, string? html, int baseVersion, bool publishAnyway, ClaimsPrincipal user)
    {
        RequireTech(user);
        var cleanTitle = CheckTitle(title);
        CheckLength(html);
        var clean = WikiHtml.Clean(html);
        if (!WikiHtml.HasContent(clean)) throw new InvalidOperationException("The page is empty. Add something before publishing.");

        await using var db = await dbFactory.CreateAsync();
        var page = await LoadEditableAsync(db, pageId, user);
        if (page.CurrentVersion != baseVersion && !publishAnyway) return new WikiPublishResult(false, page.CurrentVersion, page.PublishedBy);

        ApplyVersion(db, page, cleanTitle, clean, null, user);
        var name = NameOf(user);
        var draft = await db.WikiDrafts.FirstOrDefaultAsync(d => d.PageId == pageId && d.UserName == name);
        if (draft is not null) db.WikiDrafts.Remove(draft);
        await SaveVersionAsync(db, page, "published");
        return new WikiPublishResult(true, page.CurrentVersion, null);
    }

    public async Task<List<WikiVersionView>> HistoryAsync(int pageId, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        await LoadEditableAsync(db, pageId, user);
        return await db.WikiPageVersions.AsNoTracking().Where(v => v.PageId == pageId).OrderByDescending(v => v.Number)
            .Select(v => new WikiVersionView(v.Number, v.Title, v.Html, v.Note, v.CreatedBy, v.CreatedAt)).ToListAsync();
    }

    /// <summary>Brings back an earlier version as a new one, so the versions after it aren't lost.</summary>
    public async Task RestoreAsync(int pageId, int number, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var page = await LoadEditableAsync(db, pageId, user);
        var version = await db.WikiPageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PageId == pageId && v.Number == number)
            ?? throw new InvalidOperationException("That version no longer exists.");
        ApplyVersion(db, page, version.Title, version.Html, $"Restored version {number}", user);
        await SaveVersionAsync(db, page, $"restored version {number} of");
    }

    public async Task UpdateSettingsAsync(int pageId, int sectionId, string? minimumRole, ClaimsPrincipal user)
    {
        RequireTech(user);
        CheckRole(minimumRole);
        await using var db = await dbFactory.CreateAsync();
        var page = await LoadEditableAsync(db, pageId, user);
        if (!await db.WikiSections.AnyAsync(s => s.Id == sectionId)) throw new InvalidOperationException("Choose a section.");
        if (page.SectionId != sectionId)
        {
            page.SectionId = sectionId;
            page.SortOrder = (await db.WikiPages.Where(p => p.SectionId == sectionId).Select(p => (int?)p.SortOrder).MaxAsync() ?? -1) + 1;
        }
        page.MinimumRole = minimumRole!;
        await db.SaveChangesAsync();
    }

    public async Task MovePageAsync(int pageId, int direction, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var page = await LoadEditableAsync(db, pageId, user);
        var siblings = await db.WikiPages.Where(p => p.SectionId == page.SectionId).OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ToListAsync();
        if (Move(siblings, siblings.FindIndex(p => p.Id == pageId), direction, (p, n) => p.SortOrder = n)) await db.SaveChangesAsync();
    }

    public async Task DeletePageAsync(int pageId, ClaimsPrincipal user)
    {
        RequireManager(user);
        await using var db = await dbFactory.CreateAsync();
        db.SuppressEntityAudit = true;
        var page = await db.WikiPages.FirstOrDefaultAsync(p => p.Id == pageId);
        if (page is null) return;
        db.WikiPages.Remove(page);
        db.LogAudit("Wiki.PageDeleted", nameof(WikiPage), pageId.ToString(), $"Deleted the page {page.Title}, with its {page.CurrentVersion} version(s).");
        await db.SaveChangesAsync();
    }

    // ---- Sections ----

    public async Task SaveSectionAsync(int id, string? name, ClaimsPrincipal user)
    {
        RequireManager(user);
        var clean = name?.Trim() ?? "";
        if (clean.Length == 0) throw new InvalidOperationException("Give the section a name.");
        if (clean.Length > 80) throw new InvalidOperationException("Section names can be up to 80 characters.");

        await using var db = await dbFactory.CreateAsync();
        var others = await db.WikiSections.Where(s => s.Id != id).ToListAsync();
        if (others.Any(s => string.Equals(s.Name, clean, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"There's already a section called {clean}.");

        var section = id == 0 ? new WikiSection { SortOrder = others.Count == 0 ? 0 : others.Max(s => s.SortOrder) + 1 }
            : await db.WikiSections.FindAsync(id) ?? throw new InvalidOperationException("That section no longer exists.");
        section.Name = clean;
        if (id == 0) db.WikiSections.Add(section);
        await db.SaveChangesAsync();
    }

    public async Task MoveSectionAsync(int id, int direction, ClaimsPrincipal user)
    {
        RequireManager(user);
        await using var db = await dbFactory.CreateAsync();
        var sections = await db.WikiSections.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
        if (Move(sections, sections.FindIndex(s => s.Id == id), direction, (s, n) => s.SortOrder = n)) await db.SaveChangesAsync();
    }

    /// <summary>Only an empty section can be deleted, so pages can't be lost by accident.</summary>
    public async Task DeleteSectionAsync(int id, ClaimsPrincipal user)
    {
        RequireManager(user);
        await using var db = await dbFactory.CreateAsync();
        var section = await db.WikiSections.FindAsync(id);
        if (section is null) return;
        var pages = await db.WikiPages.CountAsync(p => p.SectionId == id);
        if (pages > 0) throw new InvalidOperationException($"{section.Name} still has {pages} page(s). Move or delete them first.");
        db.WikiSections.Remove(section);
        await db.SaveChangesAsync();
    }

    // ---- Images ----

    /// <summary>Adds a picture to the library, re-saved as a clean JPEG like map photos, which removes hidden data such as GPS.</summary>
    public async Task<WikiImageView> UploadImageAsync(string fileName, byte[] upload, ClaimsPrincipal user)
    {
        RequireTech(user);
        var processed = await Task.Run(() => MapPhotoService.Process(upload));
        var key = MapFileStore.NewStorageKey();
        await images.SaveAsync(key, processed.Jpeg, processed.Thumbnail);

        await using var db = await dbFactory.CreateAsync();
        var image = new WikiImage { StorageKey = key, FileName = Truncate(FileNames.WithoutFolders(fileName), 260), Width = processed.Width, Height = processed.Height };
        db.WikiImages.Add(image);
        await db.SaveChangesAsync();
        return ToView(image);
    }

    public async Task<List<WikiImageView>> ListImagesAsync(ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var list = await db.WikiImages.AsNoTracking().OrderByDescending(i => i.Id).ToListAsync();
        return list.Select(ToView).ToList();
    }

    // ---- Helpers ----

    private static WikiImageView ToView(WikiImage image) => new(image.StorageKey, image.FileName, $"wiki-images/{image.StorageKey}");

    private static void ApplyVersion(PcServDbContext db, WikiPage page, string title, string html, string? note, ClaimsPrincipal user)
    {
        page.CurrentVersion++;
        page.Title = title;
        page.Html = html;
        page.SearchText = WikiHtml.TextOf(html);
        page.PublishedAt = DateTime.UtcNow;
        page.PublishedBy = NameOf(user);
        db.WikiPageVersions.Add(new WikiPageVersion { PageId = page.Id, Number = page.CurrentVersion, Title = title, Html = html, Note = note });
    }

    /// <summary>Records one line in the audit log. Whole pages would bury everything else, and the history keeps them anyway.</summary>
    private static async Task SaveVersionAsync(PcServDbContext db, WikiPage page, string action)
    {
        db.SuppressEntityAudit = true;
        db.LogAudit("Wiki.Published", nameof(WikiPage), page.Id.ToString(), $"{char.ToUpperInvariant(action[0])}{action[1..]} {page.Title}: now version {page.CurrentVersion}.");
        await db.SaveChangesAsync();
    }

    private static async Task<WikiPage> LoadEditableAsync(PcServDbContext db, int pageId, ClaimsPrincipal user)
    {
        var page = await db.WikiPages.FirstOrDefaultAsync(p => p.Id == pageId) ?? throw new InvalidOperationException("That page no longer exists.");
        if (!Holds(user, page.MinimumRole)) throw new InvalidOperationException("You don't have permission to change that page.");
        return page;
    }

    private static bool Move<T>(List<T> items, int index, int direction, Action<T, int> setOrder)
    {
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= items.Count) return false;
        (items[index], items[target]) = (items[target], items[index]);
        for (var i = 0; i < items.Count; i++) setOrder(items[i], i);
        return true;
    }

    private static string Snippet(string text, string[] terms)
    {
        var at = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 60);
        var length = Math.Min(180, text.Length - start);
        return (start > 0 ? "..." : "") + text.Substring(start, length).Trim() + (start + length < text.Length ? "..." : "");
    }

    private static string CheckTitle(string? title)
    {
        var clean = title?.Trim() ?? "";
        if (clean.Length == 0) throw new InvalidOperationException("Give the page a title.");
        if (clean.Length > 150) throw new InvalidOperationException("Titles can be up to 150 characters.");
        return clean;
    }

    private static void CheckRole(string? role)
    {
        if (!PageRoles.Contains(role)) throw new InvalidOperationException("Choose who can read this page.");
    }

    private static void CheckLength(string? html)
    {
        if (html?.Length > MaxHtmlLength) throw new InvalidOperationException("This page is too long to save. Try splitting it into two.");
    }

    private static bool CanSee(ClaimsPrincipal user, string minimumRole, bool isPublished) =>
        Holds(user, minimumRole) && (isPublished || Holds(user, Roles.Tech));

    private static bool Holds(ClaimsPrincipal user, string role) => Roles.MeetsMinimum(Roles.All.Where(user.IsInRole).ToList(), role);

    private static void RequireTech(ClaimsPrincipal user)
    {
        if (!Holds(user, Roles.Tech)) throw new InvalidOperationException("Only Techs and above can change the wiki.");
    }

    private static void RequireManager(ClaimsPrincipal user)
    {
        if (!Holds(user, Roles.Manager)) throw new InvalidOperationException("Only Managers and Admins can do that.");
    }

    private static string NameOf(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Keeps wiki images in the data folder. Keys are generated here, and checked before they touch a path.</summary>
public sealed class WikiImageStore(string dataDirectory)
{
    private readonly string _folder = Path.Combine(dataDirectory, "wiki-images");

    public async Task SaveAsync(string key, byte[] image, byte[] thumbnail)
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(Path.Combine(_folder, Check(key) + ".jpg"), image);
        await File.WriteAllBytesAsync(Path.Combine(_folder, key + "-thumb.jpg"), thumbnail);
    }

    public void Delete(string key)
    {
        foreach (var name in new[] { Check(key) + ".jpg", key + "-thumb.jpg" })
        {
            var path = Path.Combine(_folder, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>An image's path, or null if the key isn't valid or there's no such image.</summary>
    public string? PathFor(string key, bool thumbnail)
    {
        if (key.Length != 32 || !key.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) return null;
        var path = Path.Combine(_folder, key + (thumbnail ? "-thumb.jpg" : ".jpg"));
        return File.Exists(path) ? path : null;
    }

    private static string Check(string key) => key.Length == 32 && key.All(Uri.IsHexDigit) ? key : throw new ArgumentException("Invalid image key.", nameof(key));
}

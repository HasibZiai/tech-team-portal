using System.Text;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;

namespace PcServ.Web.Services.Maps;

/// <summary>Where the import list lives. It's set in configuration, never typed into a page.</summary>
public sealed record MapImportOptions(string ListPath);

public enum MapImportStatus
{
    Ready,
    AlreadyImported,
    Problem
}

/// <summary>One line of the import list.</summary>
public sealed record MapImportRow(int Line, string File, string Site, string? Code, SiteType SiteType, string MapName, SiteMapKind Kind, int? Floor, string Visibility);

public sealed record MapImportPreviewRow(MapImportRow Row, MapImportStatus Status, string? Message);

public sealed record MapImportFailure(MapImportRow Row, string Message);

public sealed record MapImportResult(int Imported, int SitesCreated, int LabelsImported, IReadOnlyList<MapImportFailure> Failures);

/// <summary>
/// Imports many maps from a CSV list, which can be edited in Excel. Columns:
/// File, Site, Code, SiteType, MapName, Kind, Floor, Visibility. File paths are relative to the list.
/// Safe to run again: anything already imported is skipped, so a stopped import can just be resumed.
/// </summary>
public sealed class MapImportService(MapImportOptions options, SiteMapService maps, AppDbFactory dbFactory)
{
    private static readonly string[] RequiredColumns = ["File", "Site", "SiteType", "MapName", "Kind", "Visibility"];

    public string ListPath => options.ListPath;

    public bool ListExists => File.Exists(options.ListPath);

    /// <summary>Checks every line of the list without changing anything.</summary>
    public async Task<List<MapImportPreviewRow>> PreviewAsync()
    {
        var (rows, problems) = ReadList();

        await using var db = await dbFactory.CreateAsync();
        var existing = await db.SiteMaps.AsNoTracking()
            .Select(m => new { SiteName = m.Site!.Name, m.SourceFileName, MapName = m.Name })
            .ToListAsync();

        // A map counts as imported if its file or its name is already at that site,
        // so renaming a file later doesn't bring the same map in twice.
        var imported = existing.Select(m => Key(m.SiteName, m.SourceFileName))
            .Concat(existing.Select(m => Key(m.SiteName, m.MapName)))
            .ToHashSet();

        var preview = problems.ToList();
        foreach (var row in rows)
        {
            var path = ResolveFile(row.File);
            if (path is null)
            {
                preview.Add(new(row, MapImportStatus.Problem, "This file is outside the import folder."));
            }
            else if (!File.Exists(path))
            {
                preview.Add(new(row, MapImportStatus.Problem, "File not found."));
            }
            else if (imported.Contains(Key(row.Site, FileNames.WithoutFolders(row.File))) || imported.Contains(Key(row.Site, row.MapName)))
            {
                preview.Add(new(row, MapImportStatus.AlreadyImported, null));
            }
            else
            {
                preview.Add(new(row, MapImportStatus.Ready, null));
            }
        }
        return preview.OrderBy(p => p.Row.Line).ToList();
    }

    /// <summary>Imports everything that's ready. One bad file is recorded and doesn't stop the rest.</summary>
    public async Task<MapImportResult> ImportAsync(IProgress<(int Done, int Total, string Current)>? progress = null, CancellationToken ct = default)
    {
        var ready = (await PreviewAsync()).Where(p => p.Status == MapImportStatus.Ready).Select(p => p.Row).ToList();
        var failures = new List<MapImportFailure>();
        int imported = 0, sitesCreated = 0, labels = 0, done = 0;

        foreach (var row in ready)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, ready.Count, $"{row.Site}: {row.MapName}"));

            try
            {
                var (siteId, created) = await FindOrCreateSiteAsync(row);
                if (created) sitesCreated++;

                var bytes = await File.ReadAllBytesAsync(ResolveFile(row.File)!, ct);
                var upload = new MapUpload(siteId, null, row.MapName, row.Kind, row.Floor, FileNames.WithoutFolders(row.File), bytes, row.Visibility);

                // Drawing a map is slow work, so it runs off the page's thread to keep the page responsive.
                var result = await Task.Run(() => maps.CreateFromUploadAsync(upload), ct);
                imported++;
                labels += result.LabelCount;
            }
            catch (Exception ex) when (ex is MapFileException or IOException or DbUpdateException)
            {
                failures.Add(new(row, ex.Message));
            }

            done++;
            progress?.Report((done, ready.Count, $"{row.Site}: {row.MapName}"));
        }

        await using var db = await dbFactory.CreateAsync();
        db.LogAudit("Map.BulkImport", nameof(SiteMap), null,
            $"Imported {imported} map(s) with {labels} room label(s), and created {sitesCreated} site(s). {failures.Count} failed.");
        await db.SaveChangesAsync(CancellationToken.None);

        return new MapImportResult(imported, sitesCreated, labels, failures);
    }

    private async Task<(int Id, bool Created)> FindOrCreateSiteAsync(MapImportRow row)
    {
        await using var db = await dbFactory.CreateAsync();
        var site = await db.Sites.FirstOrDefaultAsync(s => s.Name == row.Site);
        if (site is not null) return (site.Id, false);

        site = new Site { Name = row.Site, Code = row.Code, Type = row.SiteType };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        return (site.Id, true);
    }

    private (List<MapImportRow> Rows, List<MapImportPreviewRow> Problems) ReadList()
    {
        var rows = new List<MapImportRow>();
        var problems = new List<MapImportPreviewRow>();
        if (!ListExists) return (rows, problems);

        var lines = ParseCsv(File.ReadAllText(options.ListPath, Encoding.UTF8));
        if (lines.Count == 0) return (rows, problems);

        var columns = lines[0].Select((name, index) => (name: name.Trim(), index))
            .ToDictionary(c => c.name, c => c.index, StringComparer.OrdinalIgnoreCase);
        var missing = RequiredColumns.Where(c => !columns.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            var blank = new MapImportRow(1, "", "", null, SiteType.Other, "", SiteMapKind.Other, null, Roles.Viewer);
            problems.Add(new(blank, MapImportStatus.Problem, $"The list is missing these columns: {string.Join(", ", missing)}."));
            return (rows, problems);
        }

        for (var i = 1; i < lines.Count; i++)
        {
            var values = lines[i];
            string Get(string column) => columns.TryGetValue(column, out var index) && index < values.Count ? values[index].Trim() : "";

            var errors = new List<string>();
            if (Get("File").Length == 0) errors.Add("File is empty.");
            if (Get("Site").Length == 0) errors.Add("Site is empty.");
            if (Get("MapName").Length == 0) errors.Add("MapName is empty.");
            if (!Enum.TryParse<SiteType>(Get("SiteType"), ignoreCase: true, out var siteType) || !Enum.IsDefined(siteType)) errors.Add($"Unknown SiteType \"{Get("SiteType")}\".");
            if (!Enum.TryParse<SiteMapKind>(Get("Kind"), ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) errors.Add($"Unknown Kind \"{Get("Kind")}\".");
            var visibility = Roles.All.FirstOrDefault(r => r.Equals(Get("Visibility"), StringComparison.OrdinalIgnoreCase));
            if (visibility is null) errors.Add($"Unknown Visibility \"{Get("Visibility")}\".");
            int? floor = int.TryParse(Get("Floor"), out var f) ? f : null;
            var code = Get("Code");

            var row = new MapImportRow(i + 1, Get("File"), Get("Site"), code.Length == 0 ? null : code, siteType, Get("MapName"), kind, floor, visibility ?? Roles.Admin);
            if (errors.Count > 0) problems.Add(new(row, MapImportStatus.Problem, string.Join(" ", errors)));
            else rows.Add(row);
        }
        return (rows, problems);
    }

    /// <summary>The full path of a listed file, or null if it would fall outside the list's folder.</summary>
    private string? ResolveFile(string relative)
    {
        var root = Path.GetFullPath(Path.GetDirectoryName(options.ListPath)!);
        // A list written on Windows can use backslashes. The check below still runs on the result, so "..\" can't escape.
        var full = Path.GetFullPath(FileNames.WithForwardSlashes(relative), root);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
    }

    private static string Key(string site, string fileOrMapName) => $"{site}\n{fileOrMapName}".ToUpperInvariant();

    /// <summary>Reads CSV as Excel writes it: quoted fields, doubled quotes, and commas or line breaks in quotes.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var lines = new List<List<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndLine()
        {
            EndField();
            if (fields.Any(v => v.Length > 0)) lines.Add(fields);
            fields = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') EndField();
            else if (c == '\n') EndLine();
            else if (c != '\r' && c != '﻿') field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0) EndLine();
        return lines;
    }
}

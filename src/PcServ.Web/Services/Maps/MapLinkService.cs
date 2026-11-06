using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Data;
using SkiaSharp;

#pragma warning disable CA1416

namespace PcServ.Web.Services.Maps;

public sealed record MapLinkRow(int Id, string SiteName, string SourceName, string TargetName, MapLinkStatus Status, double Score);

public sealed record MapLinkDetail(int Id, string SiteName, int SourceMapId, string SourceName, int SourceWidth, int SourceHeight,
    int TargetMapId, string TargetName, int TargetWidth, int TargetHeight, MapLinkStatus Status, double Score);

public sealed record SuggestResult(int Suggested, int ConfirmedAutomatically, IReadOnlyList<string> Failures);

/// <summary>
/// Alignments between a map holding network layers and the other maps of the same site. Once
/// confirmed, the other map shows those layers too. They're linked, not copied, so a corrected
/// name appears everywhere. Unconfirmed alignments show nothing, because a wrongly placed item is worse than none.
/// </summary>
public sealed class MapLinkService(AppDbFactory dbFactory, TestVersion testVersion, MapFileStore files)
{
    public async Task<List<MapLinkRow>> ListAsync()
    {
        await using var db = await dbFactory.CreateAsync();
        var rows = await db.MapLinks.AsNoTracking()
            .Select(l => new MapLinkRow(l.Id, l.TargetMap!.Site!.Name, l.SourceMap!.Name, l.TargetMap.Name, l.Status, l.Score))
            .ToListAsync();
        return rows.OrderBy(r => r.Status == MapLinkStatus.Confirmed).ThenBy(r => r.SiteName).ThenBy(r => r.TargetName).ToList();
    }

    public async Task<MapLinkDetail?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.MapLinks.AsNoTracking().Where(l => l.Id == id)
            .Select(l => new MapLinkDetail(l.Id, l.TargetMap!.Site!.Name, l.SourceMapId, l.SourceMap!.Name, l.SourceMap.ImageWidth, l.SourceMap.ImageHeight,
                l.TargetMapId, l.TargetMap.Name, l.TargetMap.ImageWidth, l.TargetMap.ImageHeight, l.Status, l.Score))
            .FirstOrDefaultAsync();
    }

    /// <summary>How many map pairs have no alignment yet.</summary>
    public async Task<int> CountUnalignedAsync() => (await FindUnalignedAsync()).Count;

    /// <summary>Tries to align every pair that doesn't have an alignment yet. Safe to run again.</summary>
    public async Task<SuggestResult> SuggestAsync(IProgress<(int Done, int Total, string Current)>? progress = null, CancellationToken ct = default)
    {
        var pairs = await FindUnalignedAsync();
        var failures = new ConcurrentBag<string>();
        var results = new ConcurrentBag<(Pair Pair, AlignmentResult Result)>();
        var done = 0;

        // Aligning is slow but only uses the images, so several run at once. Saving happens afterward.
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = ct };
        await Task.Run(() => Parallel.ForEach(pairs, parallel, pair =>
        {
            try
            {
                using var source = LoadBase(pair.SourceKey);
                using var target = LoadBase(pair.TargetKey);
                results.Add((pair, MapAligner.Align(source, target)));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                failures.Add($"{pair.SiteName}, {pair.TargetName}: {ex.Message}");
            }
            progress?.Report((Interlocked.Increment(ref done), pairs.Count, $"{pair.SiteName}: {pair.TargetName}"));
        }), ct);

        await using var db = await dbFactory.CreateAsync();
        db.SuppressEntityAudit = true;
        var confirmed = 0;
        foreach (var (pair, result) in results)
        {
            var t = result.Transform;
            db.MapLinks.Add(new MapLink
            {
                SourceMapId = pair.SourceId,
                TargetMapId = pair.TargetId,
                M00 = t.M00, M01 = t.M01, M02 = t.M02, M10 = t.M10, M11 = t.M11, M12 = t.M12,
                Score = result.Score,
                Status = result.IsSameDrawing ? MapLinkStatus.Confirmed : MapLinkStatus.Suggested,
            });
            if (result.IsSameDrawing) confirmed++;
        }
        db.LogAudit("MapLink.Suggested", nameof(MapLink), null, $"Suggested {results.Count} alignment(s), {confirmed} confirmed automatically as the same drawing.");
        await db.SaveChangesAsync(CancellationToken.None);

        return new SuggestResult(results.Count - confirmed, confirmed, failures.ToList());
    }

    /// <summary>
    /// The picture for checking an alignment, or null if the alignment or its images are gone. For a web
    /// endpoint, which doesn't have the Blazor sign-in context AppDbFactory uses. This read has nothing to audit.
    /// </summary>
    public async Task<byte[]?> RenderPreviewAsync(int linkId, System.Security.Claims.ClaimsPrincipal user)
    {
        await using var db = await testVersion.CreateForAsync(user);
        var link = await db.MapLinks.AsNoTracking().Where(l => l.Id == linkId)
            .Select(l => new { l.M00, l.M01, l.M02, l.M10, l.M11, l.M12, Source = l.SourceMap!.StorageKey, Target = l.TargetMap!.StorageKey })
            .FirstOrDefaultAsync();
        if (link is null || files.BaseImagePath(link.Source) is null || files.BaseImagePath(link.Target) is null) return null;

        return await Task.Run(() =>
        {
            using var source = LoadBase(link.Source);
            using var target = LoadBase(link.Target);
            return MapAligner.RenderPreview(source, target, new MapTransform(link.M00, link.M01, link.M02, link.M10, link.M11, link.M12));
        });
    }

    public async Task ConfirmAsync(int id)
    {
        await using var db = await dbFactory.CreateAsync();
        var link = await db.MapLinks.FindAsync(id) ?? throw new InvalidOperationException("That alignment no longer exists.");
        link.Status = MapLinkStatus.Confirmed;
        await db.SaveChangesAsync();
    }

    /// <summary>Replaces an alignment with one made from matching points. It stays unconfirmed until it's been checked.</summary>
    public async Task SetFromPointsAsync(int id, IReadOnlyList<AlignmentPoint> points)
    {
        await using var db = await dbFactory.CreateAsync();
        var link = await db.MapLinks.Include(l => l.SourceMap).Include(l => l.TargetMap).FirstOrDefaultAsync(l => l.Id == id)
            ?? throw new InvalidOperationException("That alignment no longer exists.");

        var t = MapAligner.FromPoints(points, link.SourceMap!.ImageWidth, link.SourceMap.ImageHeight, link.TargetMap!.ImageWidth, link.TargetMap.ImageHeight);
        (link.M00, link.M01, link.M02, link.M10, link.M11, link.M12) = (t.M00, t.M01, t.M02, t.M10, t.M11, t.M12);
        link.Status = MapLinkStatus.Suggested;
        await db.SaveChangesAsync();
    }

    /// <summary>Removes an alignment. "Find alignments" would suggest it again, so a pair can always be retried.</summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateAsync();
        var link = await db.MapLinks.FindAsync(id);
        if (link is null) return;
        db.MapLinks.Remove(link);
        await db.SaveChangesAsync();
    }

    private SKBitmap LoadBase(string storageKey)
    {
        var path = files.BaseImagePath(storageKey) ?? throw new InvalidOperationException("A map image is missing.");
        return SKBitmap.Decode(path) ?? throw new InvalidOperationException("A map image couldn't be read.");
    }

    private sealed record Pair(int SourceId, string SourceKey, int TargetId, string TargetKey, string SiteName, string TargetName);

    private async Task<List<Pair>> FindUnalignedAsync()
    {
        await using var db = await dbFactory.CreateAsync();
        var maps = await db.SiteMaps.AsNoTracking()
            .Select(m => new { m.Id, m.SiteId, m.StorageKey, m.Name, SiteName = m.Site!.Name, HasNetwork = m.Layers.Any(l => l.Kind == MapLayerKind.Network) })
            .ToListAsync();
        var existing = (await db.MapLinks.AsNoTracking().Select(l => new { l.SourceMapId, l.TargetMapId }).ToListAsync())
            .Select(l => (l.SourceMapId, l.TargetMapId)).ToHashSet();

        return (from source in maps
                where source.HasNetwork
                from target in maps
                where target.SiteId == source.SiteId && !target.HasNetwork && !existing.Contains((source.Id, target.Id))
                select new Pair(source.Id, source.StorageKey, target.Id, target.StorageKey, target.SiteName, target.Name)).ToList();
    }
}

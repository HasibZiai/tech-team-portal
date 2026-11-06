using Microsoft.EntityFrameworkCore;
using PcServ.Data;

namespace PcServ.Web.Services.Maps;

public sealed record DemoDataSummary(int Sites, int Maps);

/// <summary>Removes the fictional demo content, including any maps on the demo sites and their files.</summary>
public sealed class DemoDataService(AppDbFactory dbFactory, SiteMapService maps)
{
    public async Task<DemoDataSummary> SummarizeAsync()
    {
        await using var db = await dbFactory.CreateAsync();
        var ids = await DemoDataSeeder.DemoSiteIdsAsync(db);
        return new DemoDataSummary(ids.Count, await db.SiteMaps.CountAsync(m => ids.Contains(m.SiteId)));
    }

    public async Task<(int Sites, int Links)> RemoveAsync()
    {
        await using var db = await dbFactory.CreateAsync();
        var ids = await DemoDataSeeder.DemoSiteIdsAsync(db);

        // Maps first, through the service, so their files are removed from disk too.
        var mapIds = await db.SiteMaps.Where(m => ids.Contains(m.SiteId)).Select(m => m.Id).ToListAsync();
        foreach (var id in mapIds) await maps.DeleteAsync(id);

        return await DemoDataSeeder.RemoveAsync(db);
    }
}

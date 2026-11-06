using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Web.Services.Maps;

namespace PcServ.Web.Services;

public sealed record OverviewWork(
    int OpenRepairs, int OpenOrders, int InventoryUpdates, int ToDeprovision, int FinesToAssign, int MapItemsToCheck, int? AlignmentsToCheck,
    IReadOnlyList<RepairView> OldestRepairs, IReadOnlyList<OrderView> OldestOrders);

public sealed record OverviewData(
    OverviewWork? Work, IReadOnlyList<WikiRecentPage> RecentWiki, IReadOnlyList<LinkItem> PinnedLinks,
    int Sites, int Maps, int WikiPages, int Links);

/// <summary>What the Overview shows: work waiting across the portal, limited to what this person's role can see.</summary>
public sealed class OverviewService(AppDbFactory dbFactory, RepairService repairs, OrderService orders, SiteMapService maps, WikiService wiki)
{
    private const int ListLength = 5;

    public async Task<OverviewData> GetAsync(ClaimsPrincipal user)
    {
        var held = Roles.All.Where(user.IsInRole).ToList();
        await using var db = await dbFactory.CreateAsync();
        var work = Roles.MeetsMinimum(held, Roles.Tech) ? await WorkAsync(user, Roles.MeetsMinimum(held, Roles.Manager)) : null;

        var pinned = await db.Links.AsNoTracking().Include(l => l.Category).Where(l => l.IsPinned)
            .OrderBy(l => l.Category!.SortOrder).ThenBy(l => l.SortOrder).ThenBy(l => l.Title).Take(12).ToListAsync();
        var wikiPages = (await wiki.TreeAsync(user)).Sum(s => s.Pages.Count(p => p.IsPublished));

        return new OverviewData(work, await wiki.RecentAsync(user, ListLength), pinned,
            await db.Sites.CountAsync(s => s.IsActive), (await maps.ListAsync(user)).Count, wikiPages, await db.Links.CountAsync());
    }

    private async Task<OverviewWork> WorkAsync(ClaimsPrincipal user, bool canManage)
    {
        var allRepairs = await repairs.ListAsync(user);
        var allOrders = await orders.ListAsync(user);
        var openRepairs = allRepairs.Where(r => !RepairStatuses.IsDone(r.Status)).ToList();
        var openOrders = allOrders.Where(o => OrderStages.IsOpen(o.Status)).ToList();

        await using var db = await dbFactory.CreateAsync();
        return new OverviewWork(
            openRepairs.Count,
            openOrders.Count,
            allRepairs.Count(r => !r.InventoryIsCurrent),
            RepairService.OutOfCirculation(allRepairs).Count(r => r.DeprovisionedOn is null),
            allRepairs.Count(r => r.FineNeeded && !r.FineDone),
            await db.MapFeatures.CountAsync(f => f.NeedsReview),
            canManage ? await db.MapLinks.CountAsync(l => l.Status == MapLinkStatus.Suggested) : null,
            openRepairs.OrderBy(r => r.DateIn).ThenBy(r => r.Id).Take(ListLength).ToList(),
            openOrders.OrderBy(o => o.StatusDate).ThenBy(o => o.Id).Take(ListLength).ToList());
    }
}

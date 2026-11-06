using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Services.Maps;

namespace PcServ.Web.Services;

public sealed record OrderHistoryView(OrderStatus Status, DateOnly Date, string? Contact, string? Note, string By);

public sealed record OrderView(
    int Id, string Item, int Quantity, int SiteId, string SiteName, string? Contact, string? RequestedFor, string? Room,
    int? VendorId, string VendorName, string? VendorWebsite, bool IsOtherVendor, string? QuoteNumber, string? TrackingNumber, string? Notes,
    OrderStatus Status, DateOnly StatusDate, int? MapId, string? MapName, int? MapFeatureId, IReadOnlyList<OrderHistoryView> History);

public sealed record OrderEdit(
    string? Item, int Quantity, int SiteId, string? RequestedFor, string? Room, int? VendorId, string? OtherVendor,
    string? QuoteNumber, string? TrackingNumber, string? Notes, string? Contact = null);

/// <summary>
/// Orders and where they've got to. Orders hold staff names, so everything here is for Techs and above,
/// and that's checked on every call rather than left to the pages.
/// </summary>
public sealed class OrderService(AppDbFactory dbFactory)
{
    public const string PinLayerName = "Orders";

    public async Task<List<OrderView>> ListAsync(ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var orders = await WithDetails(db).OrderBy(o => o.StatusDate).ThenBy(o => o.Id).ToListAsync();
        return orders.Select(ToView).ToList();
    }

    public async Task<OrderView?> GetAsync(int id, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var order = await WithDetails(db).FirstOrDefaultAsync(o => o.Id == id);
        return order is null ? null : ToView(order);
    }

    public async Task<int> CreateAsync(OrderEdit edit, DateOnly today, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var order = new Order { Status = OrderStatus.Requested, StatusDate = today };
        await ApplyAsync(db, order, edit);
        order.History.Add(new OrderStatusChange { Status = OrderStatus.Requested, Date = today });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    public async Task UpdateAsync(int id, OrderEdit edit, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var order = await db.Orders.Include(o => o.MapFeature).ThenInclude(f => f!.MapLayer).ThenInclude(l => l!.SiteMap).FirstOrDefaultAsync(o => o.Id == id)
            ?? throw new InvalidOperationException("That order no longer exists.");

        // A pin belongs on a map of the order's own site.
        if (order.MapFeature is not null && order.MapFeature.MapLayer!.SiteMap!.SiteId != edit.SiteId) db.MapFeatures.Remove(order.MapFeature);

        await ApplyAsync(db, order, edit);
        await db.SaveChangesAsync();
    }

    /// <summary>Moves an order to a stage. Any stage is allowed, so mistakes can be corrected, and each change stays in the history.</summary>
    public async Task SetStatusAsync(int id, OrderStatus status, DateOnly date, string? note, ClaimsPrincipal user, string? contact = null)
    {
        RequireTech(user);
        if (!Enum.IsDefined(status)) throw new InvalidOperationException("That isn't an order stage.");
        if (date > DateOnly.FromDateTime(DateTime.Today.AddDays(1))) throw new InvalidOperationException("The date can't be in the future.");

        await using var db = await dbFactory.CreateAsync();
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id) ?? throw new InvalidOperationException("That order no longer exists.");
        if (order.Status == status) throw new InvalidOperationException($"This order is already at {OrderStages.Name(status)}.");

        order.Status = status;
        order.StatusDate = date;
        if (status == OrderStatus.SentToContact) order.Contact = Clean(contact, 120) ?? order.Contact;
        db.OrderStatusChanges.Add(new OrderStatusChange { OrderId = id, Status = status, Date = date, Contact = status == OrderStatus.SentToContact ? order.Contact : null, Note = Clean(note, 500) });
        await db.SaveChangesAsync();
    }

    /// <summary>The maps an order can be pinned to: those of its site that this user can open.</summary>
    public async Task<List<(int Id, string Name)>> MapsForSiteAsync(int siteId, ClaimsPrincipal user)
    {
        RequireTech(user);
        var held = HeldRoles(user);
        await using var db = await dbFactory.CreateAsync();
        var maps = await db.SiteMaps.AsNoTracking().Where(m => m.SiteId == siteId).OrderBy(m => m.SortOrder).ThenBy(m => m.Name)
            .Select(m => new { m.Id, m.Name, m.MinimumRole }).ToListAsync();
        return maps.Where(m => Roles.MeetsMinimum(held, m.MinimumRole)).Select(m => (m.Id, m.Name)).ToList();
    }

    /// <summary>Pins an order to a spot on a map of its site. An order has one pin, so an earlier pin is replaced.</summary>
    public async Task<(MapLayerView Layer, MapFeatureView Feature)> PlaceOnMapAsync(int orderId, int mapId, double x, double y, ClaimsPrincipal user)
    {
        RequireTech(user);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("That isn't a position on the map.");
        await using var db = await dbFactory.CreateAsync();
        var order = await db.Orders.Include(o => o.MapFeature).FirstOrDefaultAsync(o => o.Id == orderId) ?? throw new InvalidOperationException("That order no longer exists.");
        var map = await db.SiteMaps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mapId) ?? throw new InvalidOperationException("That map no longer exists.");
        if (!Roles.MeetsMinimum(HeldRoles(user), map.MinimumRole)) throw new InvalidOperationException("You don't have permission to change this map.");
        if (map.SiteId != order.SiteId) throw new InvalidOperationException("That map is for a different site than this order.");
        if (!OrderStages.IsOpen(order.Status)) throw new InvalidOperationException("Finished orders don't show on maps.");

        var layer = await db.MapLayers.FirstOrDefaultAsync(l => l.SiteMapId == mapId && l.Kind == MapLayerKind.Orders);
        if (layer is null)
        {
            var sort = await db.MapLayers.Where(l => l.SiteMapId == mapId).Select(l => (int?)l.SortOrder).MaxAsync() ?? -1;
            layer = new MapLayer { SiteMapId = mapId, Name = PinLayerName, Kind = MapLayerKind.Orders, MinimumRole = Roles.Tech, Color = "#0B7A75", SortOrder = sort + 1 };
            db.MapLayers.Add(layer);
        }

        if (order.MapFeature is not null) db.MapFeatures.Remove(order.MapFeature);
        var feature = new MapFeature { MapLayer = layer, Type = MapFeatureType.Marker, X = Math.Clamp(x, 0, 1), Y = Math.Clamp(y, 0, 1), Label = order.Item, Symbol = MapSymbols.OrderPin };
        order.MapFeature = feature;
        await db.SaveChangesAsync();

        return (new MapLayerView(layer.Id, layer.Name, layer.Kind.ToString(), layer.MinimumRole, layer.Color, layer.VisibleByDefault, [], [], layer.SiteMapId),
            new MapFeatureView(feature.Id, feature.Type.ToString(), feature.X, feature.Y, PinLabel(order), feature.Symbol, null, false, OrderId: order.Id));
    }

    public async Task RemoveFromMapAsync(int orderId, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var order = await db.Orders.Include(o => o.MapFeature).FirstOrDefaultAsync(o => o.Id == orderId);
        if (order?.MapFeature is null) return;
        db.MapFeatures.Remove(order.MapFeature);
        await db.SaveChangesAsync();
    }

    /// <summary>How many orders a site has. It doesn't reveal any orders, so it's open to the people who can delete sites.</summary>
    public async Task<int> CountForSiteAsync(int siteId)
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.Orders.CountAsync(o => o.SiteId == siteId);
    }

    /// <summary>What a pin says on the map. It's built when the map loads, so it always shows the current stage.</summary>
    public static string PinLabel(Order order) =>
        $"{order.Item}{(string.IsNullOrWhiteSpace(order.RequestedFor) ? "" : $" for {order.RequestedFor}")}: {OrderStages.Name(order.Status, order.Contact)}";

    private static IQueryable<Order> WithDetails(PcServDbContext db) => db.Orders.AsNoTracking()
        .Include(o => o.Site).Include(o => o.Vendor).Include(o => o.History)
        .Include(o => o.MapFeature).ThenInclude(f => f!.MapLayer).ThenInclude(l => l!.SiteMap);

    private static async Task ApplyAsync(PcServDbContext db, Order order, OrderEdit edit)
    {
        var item = edit.Item?.Trim() ?? "";
        if (item.Length == 0) throw new InvalidOperationException("Say what's being ordered.");
        if (item.Length > 200) throw new InvalidOperationException("The item can be up to 200 characters.");
        if (edit.Quantity is < 1 or > 100000) throw new InvalidOperationException("The quantity needs to be at least 1.");
        if (!await db.Sites.AnyAsync(s => s.Id == edit.SiteId)) throw new InvalidOperationException("Choose a site.");

        if (edit.VendorId is { } vendorId)
        {
            var vendor = await db.Vendors.FirstOrDefaultAsync(v => v.Id == vendorId) ?? throw new InvalidOperationException("That vendor no longer exists.");

            // A vendor that has since lost approval can stay on an order that already had it.
            if (!vendor.IsApproved && order.VendorId != vendorId) throw new InvalidOperationException($"{vendor.Name} is no longer an approved vendor.");
            order.VendorId = vendorId;
            order.OtherVendor = null;
        }
        else
        {
            order.OtherVendor = Clean(edit.OtherVendor, 120) ?? throw new InvalidOperationException("Choose a vendor, or enter the name of the other vendor.");
            order.VendorId = null;
        }

        order.Item = item;
        order.Quantity = edit.Quantity;
        order.SiteId = edit.SiteId;
        order.RequestedFor = Clean(edit.RequestedFor, 120);
        order.Contact = Clean(edit.Contact, 120);
        order.Room = Clean(edit.Room, 60);
        order.QuoteNumber = Clean(edit.QuoteNumber, 60);
        order.TrackingNumber = Clean(edit.TrackingNumber, 100);
        order.Notes = Clean(edit.Notes, 2000);
    }

    private static OrderView ToView(Order o) => new(
        o.Id, o.Item, o.Quantity, o.SiteId, o.Site!.Name, o.Contact, o.RequestedFor, o.Room,
        o.VendorId, o.Vendor?.Name ?? o.OtherVendor ?? "", o.Vendor?.Website, o.VendorId is null, o.QuoteNumber, o.TrackingNumber, o.Notes,
        o.Status, o.StatusDate, o.MapFeature?.MapLayer?.SiteMapId, o.MapFeature?.MapLayer?.SiteMap?.Name, o.MapFeatureId,
        o.History.OrderByDescending(h => h.Id).Select(h => new OrderHistoryView(h.Status, h.Date, h.Contact, h.Note, h.CreatedBy)).ToList());

    private static string? Clean(string? value, int max)
    {
        var clean = value?.Trim();
        return string.IsNullOrEmpty(clean) ? null : clean.Length <= max ? clean : clean[..max];
    }

    private static void RequireTech(ClaimsPrincipal user)
    {
        if (!Roles.MeetsMinimum(HeldRoles(user), Roles.Tech)) throw new InvalidOperationException("Orders are for Techs and above.");
    }

    private static List<string> HeldRoles(ClaimsPrincipal user) => Roles.All.Where(user.IsInRole).ToList();
}

/// <summary>The approved vendor list. People with Manager or above change it. Everyone who can order picks from it.</summary>
public sealed class VendorService(AppDbFactory dbFactory)
{
    public async Task<List<Vendor>> ListAsync(bool approvedOnly = false)
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.Vendors.AsNoTracking().Where(v => !approvedOnly || v.IsApproved).OrderBy(v => v.Name).ToListAsync();
    }

    public async Task SaveAsync(Vendor model, ClaimsPrincipal user)
    {
        if (!Roles.MeetsMinimum(Roles.All.Where(user.IsInRole).ToList(), Roles.Manager)) throw new InvalidOperationException("Only Managers and Admins can change the vendor list.");

        var name = model.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Give the vendor a name.");
        if (name.Length > 120) throw new InvalidOperationException("Vendor names can be up to 120 characters.");

        // The website becomes a link, so only ordinary web addresses are allowed.
        var website = model.Website?.Trim();
        if (!string.IsNullOrEmpty(website) && !(Uri.TryCreate(website, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            throw new InvalidOperationException("The website needs to be a full address, starting with https://");
        }

        await using var db = await dbFactory.CreateAsync();
        var others = await db.Vendors.Where(v => v.Id != model.Id).Select(v => v.Name).ToListAsync();
        if (others.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{name} is already on the list.");

        var vendor = model.Id == 0 ? new Vendor() : await db.Vendors.FindAsync(model.Id) ?? throw new InvalidOperationException("That vendor no longer exists.");
        vendor.Name = name;
        vendor.Website = string.IsNullOrEmpty(website) ? null : website;
        vendor.Phone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();
        vendor.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        vendor.IsApproved = model.IsApproved;
        if (vendor.Id == 0) db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
    }
}

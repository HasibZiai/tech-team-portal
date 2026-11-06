using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Web.Services;

public sealed record RepairView(
    int Id, int DeviceId, string AssetTag, string? SerialNumber, int? ModelId, string? ModelName, int SiteId, string SiteName,
    int? ReasonId, string ReasonText, string? OtherReason, string? PickupPoint, RepairStatus Status, DateOnly DateIn, DateOnly? DateOut, string? Notes,
    RepairStatus? InventoryUpdatedTo, DateOnly? InventoryUpdatedOn, string? InventoryUpdatedBy, bool FineNeeded, bool FineDone,
    DateOnly? DeprovisionedOn = null, string? DeprovisionedBy = null)
{
    public bool InventoryIsCurrent => InventoryUpdatedTo == Status;
}

public sealed record KnownDevice(string AssetTag, string? SerialNumber, int? ModelId, int? SiteId, int? OpenRepairId, bool OutOfCirculation = false, DateOnly? DeprovisionedOn = null);

public sealed record RepairCheckIn(string? AssetTag, string? SerialNumber, int? ModelId, int SiteId, int? ReasonId, string? OtherReason, string? PickupPoint, DateOnly DateIn);

public sealed record RepairEdit(
    string? SerialNumber, int? ModelId, int SiteId, int? ReasonId, string? OtherReason, string? PickupPoint,
    RepairStatus Status, DateOnly DateIn, DateOnly? DateOut, string? Notes, bool FineNeeded, bool FineDone);

/// <summary>Repairs and the devices they're for. For Techs and above, checked on every call.</summary>
public sealed class RepairService(AppDbFactory dbFactory)
{
    public async Task<List<RepairView>> ListAsync(ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var repairs = await WithDetails(db).OrderBy(r => r.DateIn).ThenBy(r => r.Id).ToListAsync();
        return repairs.Select(ToView).ToList();
    }

    public async Task<RepairView?> GetAsync(int id, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var repair = await WithDetails(db).FirstOrDefaultAsync(r => r.Id == id);
        return repair is null ? null : ToView(repair);
    }

    /// <summary>Every repair of a device, newest first.</summary>
    public async Task<List<RepairView>> HistoryAsync(int deviceId, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var repairs = await WithDetails(db).Where(r => r.DeviceId == deviceId).OrderByDescending(r => r.DateIn).ThenByDescending(r => r.Id).ToListAsync();
        return repairs.Select(ToView).ToList();
    }

    /// <summary>What's known about an asset tag, for filling in the check-in form. Null if it's new.</summary>
    public async Task<KnownDevice?> LookUpAsync(string? assetTag, ClaimsPrincipal user)
    {
        RequireTech(user);
        var tag = assetTag?.Trim();
        if (string.IsNullOrEmpty(tag)) return null;
        await using var db = await dbFactory.CreateAsync();
        var device = await db.Devices.AsNoTracking().Include(d => d.Repairs).FirstOrDefaultAsync(d => d.AssetTag == tag);
        if (device is null) return null;
        var open = device.Repairs.FirstOrDefault(r => !RepairStatuses.IsDone(r.Status));
        var latest = device.Repairs.OrderBy(r => r.Id).LastOrDefault();
        return new KnownDevice(device.AssetTag, device.SerialNumber, device.ModelId, device.SiteId, open?.Id, latest?.Status == RepairStatus.BeyondRepair, device.DeprovisionedOn);
    }

    public async Task<int> CheckInAsync(RepairCheckIn entry, ClaimsPrincipal user)
    {
        RequireTech(user);
        var tag = entry.AssetTag?.Trim() ?? "";
        if (tag.Length == 0) throw new InvalidOperationException("Enter the asset tag.");
        if (tag.Length > 40) throw new InvalidOperationException("Asset tags can be up to 40 characters.");
        CheckDate(entry.DateIn, "The date in");

        await using var db = await dbFactory.CreateAsync();
        var device = await db.Devices.Include(d => d.Repairs).FirstOrDefaultAsync(d => d.AssetTag == tag);
        if (device?.Repairs.Any(r => !RepairStatuses.IsDone(r.Status)) == true) throw new InvalidOperationException($"{device.AssetTag} is already in for repairs.");

        device ??= db.Devices.Add(new Device { AssetTag = tag }).Entity;
        ClearDeprovisioned(device);
        await ApplyDeviceAsync(db, device, entry.SerialNumber, entry.ModelId, entry.SiteId);

        var repair = new Repair { Device = device, Status = RepairStatus.InRepair, DateIn = entry.DateIn };
        await ApplyDetailsAsync(db, repair, entry.SiteId, entry.ReasonId, entry.OtherReason, entry.PickupPoint);
        db.Repairs.Add(repair);
        await db.SaveChangesAsync();
        return repair.Id;
    }

    public async Task UpdateAsync(int id, RepairEdit edit, DateOnly today, ClaimsPrincipal user)
    {
        RequireTech(user);
        if (!Enum.IsDefined(edit.Status)) throw new InvalidOperationException("That isn't a repair status.");
        CheckDate(edit.DateIn, "The date in");
        if (edit.DateOut is { } dateOut)
        {
            CheckDate(dateOut, "The date out");
            if (dateOut < edit.DateIn) throw new InvalidOperationException("The date out can't be before the date in.");
        }

        await using var db = await dbFactory.CreateAsync();
        var repair = await db.Repairs.Include(r => r.Device).FirstOrDefaultAsync(r => r.Id == id) ?? throw new InvalidOperationException("That repair no longer exists.");
        if (!RepairStatuses.IsDone(edit.Status) && RepairStatuses.IsDone(repair.Status)) await RefuseSecondOpenRepairAsync(db, repair);
        if (edit.Status != RepairStatus.BeyondRepair) ClearDeprovisioned(repair.Device!);

        await ApplyDeviceAsync(db, repair.Device!, edit.SerialNumber, edit.ModelId, edit.SiteId);
        await ApplyDetailsAsync(db, repair, edit.SiteId, edit.ReasonId, edit.OtherReason, edit.PickupPoint);
        repair.DateIn = edit.DateIn;
        repair.Notes = Clean(edit.Notes, 2000);
        repair.FineNeeded = edit.FineNeeded;
        repair.FineDone = edit.FineNeeded && edit.FineDone;
        repair.Status = edit.Status;
        repair.DateOut = RepairStatuses.IsDone(edit.Status) ? edit.DateOut ?? Latest(today, edit.DateIn) : null;
        await db.SaveChangesAsync();
    }

    /// <summary>Changes the status. Finishing a repair fills in the date out, and reopening one clears it.</summary>
    public async Task SetStatusAsync(int id, RepairStatus status, DateOnly today, ClaimsPrincipal user)
    {
        RequireTech(user);
        if (!Enum.IsDefined(status)) throw new InvalidOperationException("That isn't a repair status.");
        await using var db = await dbFactory.CreateAsync();
        var repair = await db.Repairs.Include(r => r.Device).FirstOrDefaultAsync(r => r.Id == id) ?? throw new InvalidOperationException("That repair no longer exists.");
        if (repair.Status == status) return;
        if (!RepairStatuses.IsDone(status) && RepairStatuses.IsDone(repair.Status)) await RefuseSecondOpenRepairAsync(db, repair);
        if (status != RepairStatus.BeyondRepair) ClearDeprovisioned(repair.Device!);

        repair.Status = status;
        repair.DateOut = RepairStatuses.IsDone(status) ? repair.DateOut ?? Latest(today, repair.DateIn) : null;
        await db.SaveChangesAsync();
    }

    /// <summary>Records that Inventory now matches these repairs. They're flagged again if their status changes.</summary>
    public async Task MarkInventoryUpdatedAsync(IReadOnlyCollection<int> ids, DateOnly today, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var repairs = await db.Repairs.Where(r => ids.Contains(r.Id)).ToListAsync();
        foreach (var repair in repairs)
        {
            repair.InventoryUpdatedTo = repair.Status;
            repair.InventoryUpdatedOn = today;
            repair.InventoryUpdatedBy = user.Identity?.Name;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Undoes a mistaken "updated in Inventory".</summary>
    public async Task ClearInventoryUpdatedAsync(int id, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var repair = await db.Repairs.FirstOrDefaultAsync(r => r.Id == id) ?? throw new InvalidOperationException("That repair no longer exists.");
        (repair.InventoryUpdatedTo, repair.InventoryUpdatedOn, repair.InventoryUpdatedBy) = (null, null, null);
        await db.SaveChangesAsync();
    }

    /// <summary>Records that a device has been deprovisioned in the device console.</summary>
    public async Task MarkDeprovisionedAsync(int deviceId, DateOnly today, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId) ?? throw new InvalidOperationException("That device no longer exists.");
        device.DeprovisionedOn = today;
        device.DeprovisionedBy = user.Identity?.Name;
        await db.SaveChangesAsync();
    }

    /// <summary>Undoes a mistaken "deprovisioned".</summary>
    public async Task ClearDeprovisionedAsync(int deviceId, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId) ?? throw new InvalidOperationException("That device no longer exists.");
        ClearDeprovisioned(device);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The repairs of devices that are out of circulation: their latest repair ended as "can't be repaired".
    /// A device checked in again since then is back in use, so its older repair doesn't count.
    /// </summary>
    public static List<RepairView> OutOfCirculation(IEnumerable<RepairView> repairs) => repairs
        .GroupBy(r => r.DeviceId)
        .Select(device => device.OrderBy(r => r.Id).Last())
        .Where(r => r.Status == RepairStatus.BeyondRepair)
        .OrderBy(r => r.DateOut).ThenBy(r => r.Id)
        .ToList();

    /// <summary>How many repairs a site has. It doesn't reveal any, so it's open to the people who can delete sites.</summary>
    public async Task<int> CountForSiteAsync(int siteId)
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.Repairs.CountAsync(r => r.SiteId == siteId);
    }

    /// <summary>A plain list of asset tags, one per line, for Inventory's Batch Update.</summary>
    public static string BarcodeList(IEnumerable<RepairView> repairs) =>
        string.Join("\r\n", repairs.Select(r => r.AssetTag).Distinct(StringComparer.OrdinalIgnoreCase)) + "\r\n";

    private static IQueryable<Repair> WithDetails(PcServDbContext db) => db.Repairs.AsNoTracking()
        .Include(r => r.Device).ThenInclude(d => d!.Model).Include(r => r.Site).Include(r => r.Reason);

    private static void ClearDeprovisioned(Device device) => (device.DeprovisionedOn, device.DeprovisionedBy) = (null, null);

    private static async Task RefuseSecondOpenRepairAsync(PcServDbContext db, Repair repair)
    {
        var others = await db.Repairs.Where(r => r.DeviceId == repair.DeviceId && r.Id != repair.Id).Select(r => r.Status).ToListAsync();
        if (others.Any(s => !RepairStatuses.IsDone(s))) throw new InvalidOperationException("This device already has a newer open repair.");
    }

    private static async Task ApplyDeviceAsync(PcServDbContext db, Device device, string? serialNumber, int? modelId, int siteId)
    {
        var serial = Clean(serialNumber, 60);
        if (serial is not null)
        {
            // Usually a scan that landed in the wrong field.
            var owner = await db.Devices.AsNoTracking().Where(d => d.SerialNumber == serial && d.Id != device.Id).Select(d => d.AssetTag).FirstOrDefaultAsync();
            if (owner is not null) throw new InvalidOperationException($"That serial number belongs to {owner}.");
        }
        if (modelId is { } id && !await db.RepairListItems.AnyAsync(i => i.Id == id && i.Kind == RepairListKind.Model)) throw new InvalidOperationException("Choose a model from the list.");

        device.SerialNumber = serial;
        device.ModelId = modelId;
        device.SiteId = siteId;
    }

    private static async Task ApplyDetailsAsync(PcServDbContext db, Repair repair, int siteId, int? reasonId, string? otherReason, string? pickupPoint)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId)) throw new InvalidOperationException("Choose a site.");
        if (reasonId is { } id)
        {
            if (!await db.RepairListItems.AnyAsync(i => i.Id == id && i.Kind == RepairListKind.Reason)) throw new InvalidOperationException("Choose a reason from the list.");
            repair.ReasonId = id;
            repair.OtherReason = null;
        }
        else
        {
            repair.OtherReason = Clean(otherReason, 200) ?? throw new InvalidOperationException("Choose a reason for the repair, or describe it under Other.");
            repair.ReasonId = null;
        }
        repair.SiteId = siteId;
        repair.PickupPoint = Clean(pickupPoint, 80);
    }

    private static void CheckDate(DateOnly date, string name)
    {
        if (date > DateOnly.FromDateTime(DateTime.Today.AddDays(1))) throw new InvalidOperationException($"{name} can't be in the future.");
    }

    private static DateOnly Latest(DateOnly first, DateOnly second) => first > second ? first : second;

    private static RepairView ToView(Repair r) => new(
        r.Id, r.DeviceId, r.Device!.AssetTag, r.Device.SerialNumber, r.Device.ModelId, r.Device.Model?.Name, r.SiteId, r.Site!.Name,
        r.ReasonId, r.Reason?.Name ?? r.OtherReason ?? "", r.OtherReason, r.PickupPoint, r.Status, r.DateIn, r.DateOut, r.Notes,
        r.InventoryUpdatedTo, r.InventoryUpdatedOn, r.InventoryUpdatedBy, r.FineNeeded, r.FineDone, r.Device.DeprovisionedOn, r.Device.DeprovisionedBy);

    private static string? Clean(string? value, int max)
    {
        var clean = value?.Trim();
        return string.IsNullOrEmpty(clean) ? null : clean.Length <= max ? clean : clean[..max];
    }

    private static void RequireTech(ClaimsPrincipal user)
    {
        if (!Roles.MeetsMinimum(Roles.All.Where(user.IsInRole).ToList(), Roles.Tech)) throw new InvalidOperationException("Repairs are for Techs and above.");
    }
}

/// <summary>The model and reason lists. Everyone with repair access picks from them. Managers and above change them.</summary>
public sealed class RepairListService(AppDbFactory dbFactory)
{
    public async Task<List<RepairListItem>> ListAsync(RepairListKind kind, bool activeOnly = false)
    {
        await using var db = await dbFactory.CreateAsync();
        return await db.RepairListItems.AsNoTracking().Where(i => i.Kind == kind && (!activeOnly || i.IsActive)).OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync();
    }

    public async Task SaveAsync(RepairListItem model, ClaimsPrincipal user)
    {
        if (!Roles.MeetsMinimum(Roles.All.Where(user.IsInRole).ToList(), Roles.Manager)) throw new InvalidOperationException("Only Managers and Admins can change these lists.");
        var name = model.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Enter a name.");
        if (name.Length > 80) throw new InvalidOperationException("Names can be up to 80 characters.");

        await using var db = await dbFactory.CreateAsync();
        var others = await db.RepairListItems.Where(i => i.Kind == model.Kind && i.Id != model.Id).ToListAsync();
        if (others.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"{name} is already on this list.");

        var item = model.Id == 0 ? new RepairListItem { Kind = model.Kind, SortOrder = others.Count == 0 ? 0 : others.Max(i => i.SortOrder) + 1 }
            : await db.RepairListItems.FirstOrDefaultAsync(i => i.Id == model.Id && i.Kind == model.Kind) ?? throw new InvalidOperationException("That entry no longer exists.");
        item.Name = name;
        item.IsActive = model.IsActive;
        if (item.Id == 0) db.RepairListItems.Add(item);
        await db.SaveChangesAsync();
    }
}

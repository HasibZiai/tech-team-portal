using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Security;

namespace PcServ.Web.Services;

/// <summary>Each site's staff list. It holds people's contact details, so it's for Techs and above, checked on every call.</summary>
public sealed class SiteStaffService(AppDbFactory dbFactory)
{
    public async Task<List<SiteStaffMember>> ListAsync(int siteId, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        return await db.SiteStaffMembers.AsNoTracking().Where(s => s.SiteId == siteId).OrderBy(s => s.Name).ToListAsync();
    }

    public async Task SaveAsync(SiteStaffMember model, ClaimsPrincipal user)
    {
        RequireTech(user);
        var name = model.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Enter the person's name.");
        if (name.Length > 120) throw new InvalidOperationException("Names can be up to 120 characters.");

        await using var db = await dbFactory.CreateAsync();
        if (!await db.Sites.AnyAsync(s => s.Id == model.SiteId)) throw new InvalidOperationException("That site no longer exists.");

        var others = await db.SiteStaffMembers.Where(s => s.SiteId == model.SiteId && s.Id != model.Id).ToListAsync();
        if (others.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"{name} is already on this list.");

        var member = model.Id == 0 ? new SiteStaffMember { SiteId = model.SiteId }
            : await db.SiteStaffMembers.FirstOrDefaultAsync(s => s.Id == model.Id && s.SiteId == model.SiteId) ?? throw new InvalidOperationException("That person is no longer on this list.");
        member.Name = name;
        member.JobTitle = Clean(model.JobTitle, 120);
        member.Room = Clean(model.Room, 60);
        member.Phone = Clean(model.Phone, 40);
        member.Email = Clean(model.Email, 200);
        member.IsOrderContact = model.IsOrderContact;

        // A site has one usual contact, so choosing someone new replaces the last one.
        if (member.IsOrderContact)
        {
            foreach (var other in others.Where(s => s.IsOrderContact)) other.IsOrderContact = false;
        }

        if (member.Id == 0) db.SiteStaffMembers.Add(member);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id, ClaimsPrincipal user)
    {
        RequireTech(user);
        await using var db = await dbFactory.CreateAsync();
        var member = await db.SiteStaffMembers.FindAsync(id);
        if (member is null) return;
        db.SiteStaffMembers.Remove(member);
        await db.SaveChangesAsync();
    }

    private static string? Clean(string? value, int max)
    {
        var clean = value?.Trim();
        return string.IsNullOrEmpty(clean) ? null : clean.Length <= max ? clean : clean[..max];
    }

    private static void RequireTech(ClaimsPrincipal user)
    {
        if (!Roles.MeetsMinimum(Roles.All.Where(user.IsInRole).ToList(), Roles.Tech)) throw new InvalidOperationException("Staff lists are for Techs and above.");
    }
}

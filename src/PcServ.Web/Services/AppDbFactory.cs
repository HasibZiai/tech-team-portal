using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Data;

namespace PcServ.Web.Services;

/// <summary>
/// Creates a database context stamped with the signed-in user's name, so the audit log
/// knows who made each change. Use this in components instead of injecting the DbContext:
/// <code>await using var db = await DbFactory.CreateAsync();</code>
/// </summary>
public sealed class AppDbFactory(IDbContextFactory<PcServDbContext> factory, AuthenticationStateProvider authState, TestVersion testVersion)
{
    public async Task<PcServDbContext> CreateAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();

        // The test version's database, for an Admin who has turned it on. Everyone else gets the real one.
        var db = await testVersion.CreateForAsync(state.User);
        db.CurrentUserName = state.User.Identity?.Name ?? "system";
        return db;
    }

    /// <summary>Always the real database, for things that stay real in the test version, like accounts.</summary>
    public async Task<PcServDbContext> CreateRealAsync()
    {
        var db = await factory.CreateDbContextAsync();
        var state = await authState.GetAuthenticationStateAsync();
        db.CurrentUserName = state.User.Identity?.Name ?? "system";
        return db;
    }

    private static readonly HashSet<string> StampProperties =
        [nameof(Entity.CreatedAt), nameof(Entity.CreatedBy), nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy)];

    /// <summary>
    /// Saves a record edited in a form. New records (Id 0) are added; existing ones are updated
    /// field by field so the audit log shows exactly what changed. Only simple fields are copied,
    /// never related records, so a form can't accidentally overwrite other data.
    /// </summary>
    public async Task<T> SaveAsync<T>(T model) where T : Entity, new()
    {
        await using var db = await CreateAsync();

        var isNew = model.Id == 0;
        var target = isNew
            ? new T()
            : await db.Set<T>().FindAsync(model.Id) ?? throw new InvalidOperationException("This record no longer exists. Someone may have deleted it.");

        var source = db.Entry(model).CurrentValues;
        var destination = db.Entry(target);
        foreach (var property in source.Properties)
        {
            if (property.IsPrimaryKey() || StampProperties.Contains(property.Name)) continue;
            destination.Property(property.Name).CurrentValue = source[property];
        }

        if (isNew) db.Add(target);
        await db.SaveChangesAsync();

        model.Id = target.Id;
        return target;
    }

    /// <summary>Deletes a record by id. Returns false if it was already gone.</summary>
    public async Task<bool> DeleteAsync<T>(int id) where T : Entity
    {
        await using var db = await CreateAsync();
        var target = await db.Set<T>().FindAsync(id);
        if (target is null) return false;

        db.Remove(target);
        await db.SaveChangesAsync();
        return true;
    }
}

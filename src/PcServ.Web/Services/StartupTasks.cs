using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Security;
using PcServ.Data;

namespace PcServ.Web.Services;

public static class StartupTasks
{
    /// <summary>Applies database migrations and makes sure the four roles exist.</summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PcServDbContext>();
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<TestVersion>().UpgradeIfPresentAsync();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
    }
}

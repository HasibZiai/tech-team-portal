using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using PcServ.Core.Security;
using PcServ.Data;
using PcServ.Web.Components;
using PcServ.Web.Components.Account;
using PcServ.Web.Services;
using PcServ.Web.Services.Maps;

// Docker's health check: probe the running app and exit, without starting a second copy.
if (args.Contains(HealthCheck.CommandLineFlag))
{
    var port = HealthCheck.ConfiguredPort(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    Environment.Exit(await HealthCheck.ProbeAsync(port));
}

// Used by CI: proves the PDF libraries work inside the Linux container, then exits.
if (args.Contains(MapRenderSelfTest.CommandLineFlag))
{
    Environment.Exit(MapRenderSelfTest.Run());
}

var builder = WebApplication.CreateBuilder(args);

// Settings unique to one organization, kept out of git. Environment variables still win, as they do for everything else.
builder.Configuration.AddJsonFile("district.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddSingleton<DistrictSettings>();

// ---------------------------------------------------------------------------
// Storage: everything the app saves (database, encryption keys, later uploads)
// lives in one folder so it's easy to back up. In Docker this is a volume.
// ---------------------------------------------------------------------------
var dataDirectory = Path.GetFullPath(
    builder.Configuration["PcServ:DataDirectory"] ?? "App_Data",
    builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDirectory);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? $"Data Source={Path.Combine(dataDirectory, "pcserv.db")}";

// Keep sign-in cookies valid across restarts and container rebuilds.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("PcServDashboard");

// ---------------------------------------------------------------------------
// UI
// ---------------------------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// ---------------------------------------------------------------------------
// Database
// ---------------------------------------------------------------------------
builder.Services.AddDbContextFactory<PcServDbContext>(options => options.UseSqlite(connectionString));
// A separate database of made-up data, for an Admin who turns on "Show test version". See TestVersion.
var testDatabasePath = Path.Combine(dataDirectory, "pcserv-test.db");
builder.Services.AddSingleton(sp => new TestVersion($"Data Source={testDatabasePath}", testDatabasePath, sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<IDbContextFactory<PcServDbContext>>(), sp));
// Identity needs a regular scoped DbContext; create it from the same factory.
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PcServDbContext>>().CreateDbContext());
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ---------------------------------------------------------------------------
// Accounts: local usernames and passwords only. No email, no outside sign-in services.
// ---------------------------------------------------------------------------
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
});

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;

        options.User.RequireUniqueEmail = false;
        options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";

        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<PcServDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.CanView, p => p.RequireRole(Policies.RoleMap[Policies.CanView]))
    .AddPolicy(Policies.CanEdit, p => p.RequireRole(Policies.RoleMap[Policies.CanEdit]))
    .AddPolicy(Policies.CanManage, p => p.RequireRole(Policies.RoleMap[Policies.CanManage]))
    .AddPolicy(Policies.IsAdmin, p => p.RequireRole(Policies.RoleMap[Policies.IsAdmin]));

// ---------------------------------------------------------------------------
// App services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddScoped<AppDbFactory>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddSingleton(new MapFileStore(dataDirectory));
builder.Services.AddScoped<SiteMapService>();
builder.Services.AddScoped<SampleMapService>();
builder.Services.AddScoped<DemoDataService>();
builder.Services.AddSingleton(new MapImportOptions(Path.GetFullPath(
    builder.Configuration["PcServ:MapImportList"] ?? Path.Combine(dataDirectory, "import", "map-import.csv"),
    builder.Environment.ContentRootPath)));
builder.Services.AddScoped<MapImportService>();
builder.Services.AddScoped<NetworkLayerImportService>();
builder.Services.AddScoped<MapLinkService>();
builder.Services.AddScoped<MapPhotoService>();
builder.Services.AddScoped<MapExportService>();
builder.Services.AddScoped<MapLayerService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<VendorService>();
builder.Services.AddScoped<SiteStaffService>();
builder.Services.AddScoped<RepairService>();
builder.Services.AddScoped<RepairListService>();
builder.Services.AddScoped<WikiService>();
builder.Services.AddScoped<OverviewService>();
builder.Services.AddScoped<TestVersionManager>();
builder.Services.AddSingleton(new WikiImageStore(dataDirectory));

var app = builder.Build();

await app.InitializeDatabaseAsync();

// Getting back in from the server when nobody can sign in as an Admin. See AccountRecovery.
if (AccountRecovery.IsRequested(args))
{
    Environment.Exit(await AccountRecovery.RunAsync(app.Services, args));
}

// In Docker, HTTPS is handled by the Caddy reverse proxy in front of the app.
// The app container isn't published to the network directly, so trusting the proxy's headers is safe.
var behindProxy = app.Configuration.GetValue<bool>("PcServ:BehindProxy");
if (behindProxy)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                           | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                           | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

if (!behindProxy)
{
    app.UseHttpsRedirection();
}

// Also set when the app runs without Caddy in front of it.
app.Use((context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    return next();
});

app.UseAuthentication();
// Runs before authorization so a brand-new install goes to /setup instead of the sign-in page.
app.UseMiddleware<AppGateMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();

// Map images aren't in the public web root: they're only served through this signed-in endpoint.
app.MapGet("/map-files/{mapId:int}/base", async (int mapId, SiteMapService maps, HttpContext context) =>
{
    var path = await maps.BaseImagePathAsync(mapId, context.User);
    if (path is null) return Results.NotFound();

    context.Response.Headers.CacheControl = "private, max-age=3600";
    return Results.File(path, "image/png");
}).RequireAuthorization(Policies.CanView);

// Wiki images. The random key in the address is what keeps them private to people who can read the page.
app.MapGet("/wiki-images/{key}", (string key, bool? thumb, WikiImageStore store, HttpContext context) =>
{
    var path = store.PathFor(key, thumb == true);
    if (path is null) return Results.NotFound();

    context.Response.Headers.CacheControl = "private, max-age=86400";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(path, "image/jpeg");
}).RequireAuthorization(Policies.CanView);

// Photos on map items. Each request checks the user can see the item the photo belongs to.
app.MapGet("/map-photos/{photoId:int}", async (int photoId, bool? thumb, MapPhotoService photos, HttpContext context) =>
{
    var path = await photos.PhotoPathAsync(photoId, thumb == true, context.User);
    if (path is null) return Results.NotFound();

    context.Response.Headers.CacheControl = "private, max-age=3600";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(path, "image/jpeg");
}).RequireAuthorization(Policies.CanView);

// The picture for checking an alignment. It combines two maps, so it's for managers only.
app.MapGet("/map-links/{linkId:int}/preview", async (int linkId, MapLinkService links, HttpContext context) =>
{
    var png = await links.RenderPreviewAsync(linkId, context.User);
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
}).RequireAuthorization(Policies.CanManage);

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.Run();

/// <summary>Exposed so integration tests can start the app.</summary>
public partial class Program;

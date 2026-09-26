using Docker.DotNet;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using ZuloOne.ControlPlane;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Billing;
using ZuloOne.ControlPlane.Infra;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Provisioning.Demo;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Two ways in, both cryptographically verified, either sufficient: OpenID
// Connect against login.getzulo.com for the normal path and a break-glass
// operator for when the directory is what is broken. See Auth/AuthSetup.cs.
builder.Services.AddControlPlaneAuth(
    builder.Configuration,
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger("ControlPlane.Auth"));

if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo("/home/app/.aspnet/DataProtection-Keys"))
        .SetApplicationName("ZuloOne.ControlPlane");
}

// Protects bcrypt from being used as a CPU sink. It is NOT the security control —
// the per-account lockout is, because it cannot be sidestepped by rotating source
// addresses.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("operator-login", context => RateLimitPartition.GetFixedWindowLimiter(
        // CF-Connecting-IP first. Behind Cloudflare, RemoteIpAddress is an edge
        // address shared by every request in the world, so partitioning on it is
        // worse than not rate limiting at all: one attacker exhausts the bucket the
        // real operator needs.
        context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // The customer portal's anonymous endpoints. Same partitioning reasoning as
    // above — CF-Connecting-IP first, because behind Cloudflare RemoteIpAddress is
    // an edge address shared by the world.
    options.AddPolicy(ZuloOne.ControlPlane.Portal.PortalRateLimit.Policy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                // Looser than the operator's five, because one window covers
                // register, verify, sign in and reset, and a real person touches
                // several of those in a minute. The control that actually stops a
                // determined attacker is the per-account lockout, which cannot be
                // sidestepped by rotating addresses; this protects the CPU.
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// In production the control plane serves the dashboard itself (same origin); this
// exists so the dashboard can be run from a Vite dev server against it.
const string DashboardCors = "DashboardCors";
var dashboardOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(DashboardCors, policy =>
    policy.WithOrigins(dashboardOrigins).AllowAnyHeader().AllowAnyMethod()));

// The registry: the control plane's own database, deliberately separate from
// every tenant's — losing a tenant must not lose the record of the fleet.
builder.Services.AddDbContext<ControlPlaneDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("ControlPlane")
        ?? throw new InvalidOperationException(
            "Connection string 'ControlPlane' is required — it is where the fleet is recorded.")));

builder.Services.Configure<FleetSettings>(builder.Configuration.GetSection("Fleet"));
// Read through this, not through IOptions<FleetSettings>: the bound options are
// frozen at startup, and the settings screen writes to a table. Singleton because
// it holds no state of its own — both of its sources are singletons.
builder.Services.AddSingleton<FleetConfig>();
builder.Services.Configure<DemoSettings>(builder.Configuration.GetSection("Demo"));
// Same split as FleetConfig: policy from the settings store so the panel can
// change it, the one credential bound at startup from cp.env.
builder.Services.AddSingleton<DemoConfig>();
builder.Services.Configure<BillingSettings>(builder.Configuration.GetSection("Billing"));
builder.Services.AddSingleton<BillingConfig>();
builder.Services.Configure<TenantDatabaseSettings>(builder.Configuration.GetSection("TenantDatabase"));
builder.Services.Configure<TenantLogSettings>(builder.Configuration.GetSection("TenantLogs"));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<TenantLogStore>();
builder.Services.Configure<ControlPlaneMailSettings>(builder.Configuration.GetSection("Mail"));
builder.Services.Configure<PatroniSettings>(builder.Configuration.GetSection("Patroni"));

// Patroni answers in milliseconds when it answers at all; a node that is down must
// fail fast so the client can try the next one rather than stall the whole page.
builder.Services.AddHttpClient("patroni", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddScoped<PatroniClient>();

// Docker is how tenants actually run. Docker:Host lets the daemon be reached
// through a socket proxy later (§12) without touching this code.
builder.Services.AddSingleton<IDockerClient>(_ =>
{
    var host = builder.Configuration["Docker:Host"];
    var config = string.IsNullOrWhiteSpace(host)
        ? new DockerClientConfiguration()
        : new DockerClientConfiguration(new Uri(host));
    return config.CreateClient();
});

// Probing a booting tenant must fail fast rather than stall the provisioning loop.
builder.Services.AddHttpClient("tenant", client => client.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddScoped<TenantDatabaseProvisioner>();
builder.Services.AddScoped<TenantLogDatabaseProvisioner>();
builder.Services.AddScoped<TenantContainerService>();
builder.Services.AddScoped<TenantCloneService>();
builder.Services.AddScoped<DemoPool>();
builder.Services.AddSingleton<DemoNudge>();
builder.Services.AddScoped<TenantHealthProbe>();
builder.Services.AddScoped<TenantStatsService>();
builder.Services.AddScoped<TenantModelsService>();
builder.Services.AddScoped<RegistryModelCatalog>();
builder.Services.AddScoped<TenantUpgradeService>();
builder.Services.AddScoped<ImageTreeReader>();
builder.Services.AddScoped<TenantApiClient>();
builder.Services.AddScoped<CommercialBooks>();
builder.Services.AddScoped<TenantAdminService>();
builder.Services.AddScoped<TenantInviteService>();
builder.Services.AddScoped<TenantProvisioner>();

// The customer portal: the same fleet, seen by the people who pay for one stand
// of it. Off unless Portal:Enabled — see PortalSettings for why that default is
// false and what has to be true at the edge before it is turned on.
builder.Services.Configure<ZuloOne.ControlPlane.Portal.PortalSettings>(
    builder.Configuration.GetSection("Portal"));
builder.Services.AddScoped<ZuloOne.ControlPlane.Portal.PortalMailer>();
builder.Services.AddScoped<ZuloOne.ControlPlane.Portal.TenantClaimService>();

// Everything that takes minutes goes through one durable queue: provisioning today,
// plus snapshots, restores, upgrades, backups and switchovers. The jobs are ROWS,
// so a restart neither loses queued work nor leaves a job claiming to run forever —
// see Jobs/JobQueue.cs. Serial by design, which is also the mutual exclusion a
// restore needs against a backup.
builder.Services.AddSingleton<JobChannel>();
builder.Services.AddScoped<IJobQueue, JobQueue>();
builder.Services.AddScoped<IJobHandler, ProvisionJobHandler>();
builder.Services.AddScoped<IJobHandler, AdoptJobHandler>();
builder.Services.AddScoped<IJobHandler, SnapshotJobHandler>();
builder.Services.AddScoped<IJobHandler, RestoreJobHandler>();
builder.Services.AddScoped<IJobHandler, SwapJobHandler>();
builder.Services.AddScoped<IJobHandler, UpgradeJobHandler>();
builder.Services.AddScoped<IJobHandler, RolloutJobHandler>();
builder.Services.AddScoped<LiveSnapshotRestore>();
builder.Services.AddScoped<IJobHandler, InstallModelsJobHandler>();
builder.Services.AddScoped<IJobHandler, UninstallModelsJobHandler>();
builder.Services.AddScoped<IJobHandler, CompileModelsJobHandler>();
builder.Services.AddScoped<IJobHandler, RecreateJobHandler>();
builder.Services.AddScoped<IJobHandler, DemoProvisionJobHandler>();
builder.Services.AddScoped<IJobHandler, DemoTemplateJobHandler>();
builder.Services.AddScoped<IJobHandler, PruneJobHandler>();
builder.Services.AddScoped<IJobHandler, RegistrySnapshotJobHandler>();
builder.Services.AddSingleton<ClusterBackupGate>();
builder.Services.AddSingleton<NodePruneGate>();
builder.Services.AddScoped<IJobHandler, BackupJobHandler>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<DemoPoolService>();
builder.Services.AddHostedService<BillingSweepService>();
// Only ENQUEUES: a registry dump, then a prune. Both run through the queue, so
// they inherit the one-at-a-time execution that keeps a delete away from a dump
// being written or a restore reading one.
builder.Services.AddHostedService<PruneScheduler>();
builder.Services.AddHostedService<ZuloOne.ControlPlane.Infra.PanelHeartbeat>();

// The image registry is plain HTTP on the LAN and answers instantly or not at all.
builder.Services.AddHttpClient("registry", client => client.Timeout = TimeSpan.FromSeconds(8));

// pg_dump / pg_restore, run as child processes. A logical dump needs no shell on a
// database node: the panel already routes to Postgres and reads every tenant it
// manages.
builder.Services.Configure<SnapshotSettings>(builder.Configuration.GetSection("Snapshots"));
builder.Services.AddScoped<PgTools>();
builder.Services.AddScoped<SnapshotWriter>();

// Singleton: the override layer is read on the provisioning path, so reads must
// be free. Populated once after the migration below and reloaded on every write.
builder.Services.AddSingleton<ZuloOne.ControlPlane.Settings.SettingsStore>();

var app = builder.Build();

// Fold the configured extras into the reserved set before anything can provision.
// ReservedSlugs is static because the check has to be reachable from the API, the
// provisioner and any future tool without threading options through all three —
// so it is seeded once, here, rather than resolved per request.
ReservedSlugs.Configure(app.Services.GetRequiredService<IOptions<FleetSettings>>().Value.AdditionalReservedSlugs);

// The registry schema is the control plane's own, so a fresh deployment should
// not need a manual step.
//
// Migrations, NOT EnsureCreated. EnsureCreated builds the schema only when the
// database has no tables at all: on a registry that already holds Tenants it
// returns false and creates nothing, so a new column or table would compile,
// deploy, boot, pass /health, and then throw 42P01 at the first query touching
// it — which is the worst possible moment to find out.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.MigrateAsync();
    await OperatorSeeder.SeedAsync(
        scope.ServiceProvider,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ControlPlane.Operator"));
}

// After the migration, so the table it reads exists on a first boot. Everything
// downstream asks the store rather than IOptions, and an empty override table
// simply means every key falls through to cp.env exactly as before.
await app.Services.GetRequiredService<ZuloOne.ControlPlane.Settings.SettingsStore>().ReloadAsync();

// Say out loud whether the customer portal answers, because nothing else will.
// Its endpoints 404 when it is off, which is indistinguishable from a routing
// mistake to whoever is testing the sign-up page.
{
    var portal = app.Services
        .GetRequiredService<IOptions<ZuloOne.ControlPlane.Portal.PortalSettings>>().Value;
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ControlPlane.Portal");

    if (!portal.Enabled)
    {
        log.LogInformation("Customer portal is OFF (Portal:Enabled) — /api/portal answers 404");
    }
    else if (string.IsNullOrWhiteSpace(portal.PublicUrl))
    {
        // Not a warning but an error: the portal is serving, and every letter it
        // sends carries a link with no host. People can register and then cannot
        // finish, which looks like a mail delivery problem and is not one.
        log.LogError(
            "Customer portal is ON but Portal:PublicUrl is empty — verification and reset links will have no host and nobody will be able to finish signing up");
    }
    else
    {
        log.LogWarning(
            "Customer portal is ON at {Url}. It is reachable by people who are not staff, so Cloudflare Access must be configured to BYPASS /api/portal and /portal — otherwise Access blocks every customer before the request arrives here.",
            portal.PublicUrl);
    }
}

// Static files BEFORE authentication: they are middleware rather than endpoints,
// so they bypass the authorization policy entirely and the dashboard can load its
// own assets without a credential. That is what lets a login screen render at all.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(DashboardCors);

// Explicit, because endpoint-scoped rate limiting needs routing to have run and
// minimal hosting's implicit insertion point is not guaranteed to be before it.
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Version, not just liveness: it is what makes the release pipeline able to
// assert that the tag, the assembly and the running container agree. A bare
// {status:"ok"} gives the smoke test nothing to compare.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = AppBuild.Version,
    // The commit, because the version alone does not identify the code: outside a
    // release the panel's version is 2026.0.<CI run number>, and a run number says
    // nothing about what changed. This is what a bug report needs to carry.
    build = AppBuild.Revision,
    startedUtc = AppBuild.StartedUtc,
})).AllowAnonymous();

// `{*path}` explicitly, NOT the default MapFallback pattern. That default is
// `{*path:nonfile}`, which refuses to match anything whose last segment contains
// a dot — so a missing /favicon.ico or a stale /assets/index-abc.js reaches no
// endpoint at all, the fallback authorization policy applies, and the answer is
// 401. A missing asset reporting "Unauthorized" sends whoever debugs it hunting
// an authentication bug that does not exist.
//
// The /api guard is load-bearing too. Without it an unmatched API route returns
// index.html with a 200, so a caller sees HTML where it expected JSON and the
// dashboard looks empty rather than broken.
app.MapFallback("{*path}", async context =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // File-like and not on disk means a MISSING ASSET, not a client-side route.
    // Serving index.html for it would make a half-built deployment look like a
    // working one — the browser would get HTML where it asked for JavaScript.
    var last = path.Value?.Split('/').LastOrDefault();
    if (last is not null && last.Contains('.'))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var index = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");

    // /portal is a SECOND single-page app, not a route inside the dashboard.
    // Without this branch the dashboard's index.html would answer at
    // /portal/verify, and the customer clicking the link in their e-mail would
    // land on an operator login screen — carrying their verification token in the
    // query string of a page that has no idea what to do with it.
    if (path.StartsWithSegments("/portal"))
    {
        var portalIndex = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "portal.html");
        if (File.Exists(portalIndex))
        {
            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(portalIndex);
            return;
        }

        // Falling through to the dashboard here would be worse than a 404: it
        // would look like the portal worked and then behave like the panel.
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (!File.Exists(index))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(index);
}).AllowAnonymous();

app.Run();

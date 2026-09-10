using Docker.DotNet;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using ZuloOne.ControlPlane;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Infra;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Two ways in, both cryptographically verified, either sufficient: Cloudflare
// Access for the normal path and a break-glass operator for when Cloudflare is
// what is broken. See Auth/AuthSetup.cs.
builder.Services.AddControlPlaneAuth(
    builder.Configuration,
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger("ControlPlane.Auth"));

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
builder.Services.Configure<TenantDatabaseSettings>(builder.Configuration.GetSection("TenantDatabase"));
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
builder.Services.AddScoped<TenantContainerService>();
builder.Services.AddScoped<TenantHealthProbe>();
builder.Services.AddScoped<TenantStatsService>();
builder.Services.AddScoped<TenantModelsService>();
builder.Services.AddScoped<TenantAdminService>();
builder.Services.AddScoped<TenantInviteService>();
builder.Services.AddScoped<TenantProvisioner>();

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
builder.Services.AddScoped<IJobHandler, PruneJobHandler>();
builder.Services.AddScoped<IJobHandler, RegistrySnapshotJobHandler>();
builder.Services.AddHostedService<JobWorker>();
// Only ENQUEUES: a registry dump, then a prune. Both run through the queue, so
// they inherit the one-at-a-time execution that keeps a delete away from a dump
// being written or a restore reading one.
builder.Services.AddHostedService<PruneScheduler>();

// The image registry is plain HTTP on the LAN and answers instantly or not at all.
builder.Services.AddHttpClient("registry", client => client.Timeout = TimeSpan.FromSeconds(8));

// pg_dump / pg_restore, run as child processes. A logical dump needs no shell on a
// database node: the panel already routes to Postgres and reads every tenant it
// manages.
builder.Services.Configure<SnapshotSettings>(builder.Configuration.GetSection("Snapshots"));
builder.Services.AddScoped<PgTools>();

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
    if (!File.Exists(index))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(index);
}).AllowAnonymous();

app.Run();

using Docker.DotNet;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

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
builder.Services.Configure<TenantDatabaseSettings>(builder.Configuration.GetSection("TenantDatabase"));
builder.Services.Configure<ControlPlaneMailSettings>(builder.Configuration.GetSection("Mail"));

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
builder.Services.AddScoped<TenantInviteService>();
builder.Services.AddScoped<TenantProvisioner>();

// Provisioning takes minutes and must not depend on the caller staying connected.
// Singleton queue, hosted worker, one scope per tenant — see ProvisioningQueue.cs.
builder.Services.AddSingleton<ProvisioningQueue>();
builder.Services.AddSingleton<IProvisioningQueue>(sp => sp.GetRequiredService<ProvisioningQueue>());
builder.Services.AddHostedService<ProvisioningWorker>();

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
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
})).AllowAnonymous();

// The /api guard is load-bearing. Without it an unmatched API route returns
// index.html with a 200, so a caller sees HTML where it expected JSON and the
// dashboard looks empty rather than broken.
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
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

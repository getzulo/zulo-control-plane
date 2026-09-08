using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

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
// KNOW THE LIMIT: EnsureCreated creates the schema only when the database has no
// tables at all. On a registry that already holds Tenants it returns false and
// creates NOTHING — so a new column or table added to the model compiles,
// deploys, boots and passes /health, then throws 42P01 at the first query that
// touches it. That is the worst moment to find out.
//
// It is still correct today because the registry has never been deployed. The
// first change to this model after it IS deployed must convert to migrations
// first; do not add a property and assume this line will apply it.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.EnsureCreatedAsync();
}

// Static files BEFORE the API: they are middleware rather than endpoints, so
// they bypass any authorization policy and the dashboard can load its own assets
// without a credential. That is what lets a login screen render at all.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(DashboardCors);
app.MapControllers();

// Version, not just liveness: it is what makes the release pipeline able to
// assert that the tag, the assembly and the running container agree. A bare
// {status:"ok"} gives the smoke test nothing to compare.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
}));

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
});

app.Run();

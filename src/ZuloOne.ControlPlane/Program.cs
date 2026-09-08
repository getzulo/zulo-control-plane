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

var app = builder.Build();

// Fold the configured extras into the reserved set before anything can provision.
// ReservedSlugs is static because the check has to be reachable from the API, the
// provisioner and any future tool without threading options through all three —
// so it is seeded once, here, rather than resolved per request.
ReservedSlugs.Configure(app.Services.GetRequiredService<IOptions<FleetSettings>>().Value.AdditionalReservedSlugs);

// The registry schema is the control plane's own, so a fresh deployment should
// not need a manual step. EnsureCreated is enough while the schema is one table;
// it becomes a migration once the shape starts changing under a live fleet.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.EnsureCreatedAsync();
}

app.UseCors(DashboardCors);
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

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

// The registry schema is the control plane's own, so a fresh deployment should
// not need a manual step. EnsureCreated is enough while the schema is one table;
// it becomes a migration once the shape starts changing under a live fleet.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.EnsureCreatedAsync();
}

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

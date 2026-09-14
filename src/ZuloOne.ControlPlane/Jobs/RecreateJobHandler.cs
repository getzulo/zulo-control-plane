using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// Recreates a tenant container so boot environment (developer stand) applies.
/// Same image, same database — only env and labels are rewritten.
/// </summary>
public sealed class RecreateJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly FleetConfig _fleet;

    public RecreateJobHandler(
        ControlPlaneDbContext db,
        TenantContainerService containers,
        TenantHealthProbe health,
        TenantDatabaseProvisioner databases,
        FleetConfig fleet)
    {
        _db = db;
        _containers = containers;
        _health = health;
        _databases = databases;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.Recreate;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var id = context.Job.TenantId ?? throw new InvalidOperationException("A Recreate job must name a tenant.");
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName) || string.IsNullOrWhiteSpace(tenant.DatabaseRole))
            throw new InvalidOperationException($"'{tenant.Slug}' has no database — provision it first.");

        tenant.Status = TenantStatus.Provisioning;
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await context.StepAsync($"Recreating {tenant.Slug} on {tenant.ImageTag}", 30, ct);
        var connection = _databases.TenantConnectionString(
            tenant.DatabaseName!, tenant.DatabaseRole!, tenant.DatabasePassword ?? string.Empty);
        tenant.ContainerId = await _containers.RunAsync(tenant, connection, ct);
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await context.StepAsync("Waiting for it to come up", 70, ct);
        var ready = await _health.WaitUntilReadyAsync(
            _containers.HostFor(tenant.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
        if (!ready)
        {
            tenant.Status = TenantStatus.Failed;
            tenant.Health = TenantHealth.Down;
            tenant.LastError = "Did not come up after recreate. Data is untouched — only the container and its env changed.";
            tenant.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw new TimeoutException(tenant.LastError);
        }

        tenant.Status = TenantStatus.Active;
        tenant.Health = TenantHealth.Ok;
        tenant.LastHealthAt = DateTime.UtcNow;
        tenant.LastError = null;
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await context.StepAsync($"{tenant.Slug} is up", 100, ct);
    }
}

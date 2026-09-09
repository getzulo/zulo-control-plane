using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// Brings a tenant that was deployed by hand under the panel's management.
///
/// <para>
/// The first tenant on this fleet predates the control plane: it has a database, a
/// role and a running container, and the registry has never heard of it. That makes
/// it invisible in the fleet list and excluded from everything the panel does —
/// no snapshots, no restore, no upgrades. Measured on it: the panel could read 0
/// of its 110 tables, because role membership only ever arrived as a side effect
/// of creating the role itself.
/// </para>
///
/// <para>
/// Adoption resets the tenant's database password, which is the point: the panel
/// must hold credentials it can prove work, and a password nobody recorded is not
/// one. The container is then recreated from the registry, so from here on it is
/// described by the same row and the same labels as every provisioned tenant.
/// </para>
/// </summary>
public sealed class AdoptJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly FleetConfig _fleet;

    public AdoptJobHandler(
        ControlPlaneDbContext db,
        TenantDatabaseProvisioner databases,
        TenantContainerService containers,
        TenantHealthProbe health,
        FleetConfig fleet)
    {
        _db = db;
        _databases = databases;
        _containers = containers;
        _health = health;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.Adopt;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var id = context.Job.TenantId ?? throw new InvalidOperationException("An Adopt job must name the registry row it is completing.");
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");

        await context.StepAsync($"Taking over the credentials for {tenant.DatabaseRole}", 15, ct);
        string password;
        try
        {
            password = await _databases.ResetPasswordAsync(tenant.DatabaseRole!, ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42501")
        {
            // PostgreSQL 16+ requires ADMIN OPTION on a role to alter it; plain
            // membership is not enough, and the panel cannot grant itself the
            // option. That is the correct boundary — it must not be able to seize
            // an arbitrary role — so this needs one deliberate act by a superuser
            // and the message says exactly which.
            throw new InvalidOperationException(
                $"Not allowed to change the password of '{tenant.DatabaseRole}'. Membership alone does not permit it. " +
                $"Run once as a superuser:  GRANT \"{tenant.DatabaseRole}\" TO \"{_databases.AdminUserName}\" WITH ADMIN OPTION;  then adopt again. " +
                $"The tenant has not been touched.", ex);
        }
        tenant.DatabasePassword = password;
        await _db.SaveChangesAsync(ct);

        await context.StepAsync("Granting the panel access to the database", 30, ct);
        await _databases.AdoptGrantsAsync(tenant.DatabaseName!, tenant.DatabaseRole!, ct);
        await context.LogAsync(
            "The panel is now a member of the tenant's role and holds CONNECT by name, so snapshots read the whole database rather than silently nothing.", ct);

        await context.StepAsync($"Recreating the container on {tenant.ImageTag}", 50, ct);
        var connectionString = _databases.TenantConnectionString(tenant.DatabaseName!, tenant.DatabaseRole!, password);

        // Remove the ORIGINAL container by id first.
        //
        // RunAsync only clears a container named `zuloone-tenant-<slug>`, and a
        // hand-deployed one is named whatever deployed it — the first tenant here
        // came from a compose stack and is `zuloone-prod-tenant-t1-1`. Leaving it
        // running would put TWO containers behind Traefik routers matching the same
        // Host rule, with nothing deciding which serves the tenant.
        var original = tenant.ContainerId;
        if (!string.IsNullOrWhiteSpace(original))
        {
            await _containers.RemoveAsync(original!, ct);
            await context.LogAsync($"Removed the original container {original![..Math.Min(12, original.Length)]}.", ct);
        }

        tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, ct);
        await _db.SaveChangesAsync(ct);

        await context.StepAsync($"Waiting for {tenant.Slug} to answer", 75, ct);
        var ready = await _health.WaitUntilReadyAsync(
            _containers.HostFor(tenant.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
        if (!ready)
        {
            tenant.Status = TenantStatus.Failed;
            tenant.Health = TenantHealth.Down;
            tenant.LastError = "Did not come up after adoption. Its data is untouched — the container and its credentials are what changed.";
            await _db.SaveChangesAsync(CancellationToken.None);
            throw new TimeoutException(tenant.LastError);
        }

        tenant.Status = TenantStatus.Active;
        tenant.Health = TenantHealth.Ok;
        tenant.LastHealthAt = DateTime.UtcNow;
        tenant.LastError = null;
        await _db.SaveChangesAsync(ct);

        await context.StepAsync($"{tenant.Slug} is managed", 100, ct);
        await context.LogAsync(
            "Signed-in users were logged out: the container now runs with a signing key the registry holds, and the old one was not recorded anywhere.", ct);
    }
}

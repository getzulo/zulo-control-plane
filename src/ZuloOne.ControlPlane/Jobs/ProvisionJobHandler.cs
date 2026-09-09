using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// Builds a registered tenant. The first job kind, and the one the others are
/// modelled on: the registry row is claimed synchronously on the request, and
/// everything slow — database, container, first boot, seeding the administrator —
/// happens here.
/// </summary>
public sealed class ProvisionJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantProvisioner _provisioner;

    public ProvisionJobHandler(ControlPlaneDbContext db, TenantProvisioner provisioner)
    {
        _db = db;
        _provisioner = provisioner;
    }

    public JobKind Kind => JobKind.Provision;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var tenantId = context.Job.TenantId
            ?? throw new InvalidOperationException("A Provision job must name the tenant it is building.");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            // Deleted between queueing and running. Not an error worth a stack trace,
            // but the job must not report success for work it did not do.
            ?? throw new InvalidOperationException($"Tenant {tenantId} is no longer in the registry.");

        await _provisioner.BuildAsync(tenant, context, ct);
    }
}

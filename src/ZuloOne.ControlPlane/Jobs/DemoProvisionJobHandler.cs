using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Provisioning.Demo;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Builds one warm demo workspace from the blessed template.</summary>
public sealed class DemoProvisionJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantCloneService _clone;
    private readonly TenantAdminService _admin;
    private readonly TenantProvisioner _provisioner;
    private readonly TenantContainerService _containers;
    private readonly DemoConfig _config;
    private readonly SnapshotSettings _snapshots;
    private readonly FleetConfig _fleet;

    public DemoProvisionJobHandler(
        ControlPlaneDbContext db,
        TenantCloneService clone,
        TenantAdminService admin,
        TenantProvisioner provisioner,
        TenantContainerService containers,
        DemoConfig config,
        IOptions<SnapshotSettings> snapshots,
        FleetConfig fleet)
    {
        _db = db;
        _clone = clone;
        _admin = admin;
        _provisioner = provisioner;
        _containers = containers;
        _config = config;
        _snapshots = snapshots.Value;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.DemoProvision;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        if (!_config.Enabled)
            throw new InvalidOperationException("Demo workspaces are disabled.");

        var snapshotId = _config.TemplateSnapshotId
            ?? throw new InvalidOperationException("Demo:TemplateSnapshotId is empty. Build a template first.");
        var snapshot = await _db.Snapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
            ?? throw new InvalidOperationException("The configured demo template snapshot is no longer registered.");
        var file = Path.Combine(_snapshots.Path, snapshot.FileName);
        if (!File.Exists(file))
            throw new InvalidOperationException($"The demo template file {snapshot.FileName} is missing from disk.");

        var golden = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == _config.GoldenSlug, ct)
            ?? throw new InvalidOperationException($"Golden tenant '{_config.GoldenSlug}' is not registered.");

        var live = await _db.Tenants.CountAsync(
            t => t.Demo == TenantDemo.Pooled || t.Demo == TenantDemo.Claimed, ct);
        if (live >= _config.MaxConcurrent)
            throw new InvalidOperationException("The demo workspace ceiling has been reached.");

        var slug = await UniqueSlugAsync(ct);
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Slug = slug,
            DisplayName = "Demo workspace",
            ImageTag = string.IsNullOrWhiteSpace(snapshot.ImageTag) ? _fleet.DefaultImage : snapshot.ImageTag!,
            Models = golden.Models,
            Plan = "demo",
            Demo = TenantDemo.Pooled,
            ExpiresAt = DemoLifetime.ExpiryForPooled(now, _config.PoolMaxAgeHours),
            Origin = TenantOrigin.Provisioned,
            DeveloperStand = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.Job.TenantId = tenant.Id;
        context.Job.TenantSlug = slug;
        await context.StepAsync($"Building pooled demo {slug}", 5, ct);
        await _clone.CloneAsync(
            context, tenant, file, snapshot.SizeBytes, "pooled demo", ct, _config.Limits);

        try
        {
            var reset = await _admin.ResetPasswordAsync(
                tenant, _config.UserName, ct, mustChangePassword: false);
            if (!reset.Found)
                throw new InvalidOperationException(
                    $"Demo account '{_config.UserName}' was not found in the template.");

            // The plaintext exists only here and in the registry until the demo is
            // claimed. It is never written to a job log.
            tenant.AdminPasswordOnce = reset.Password;
            tenant.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            await DeleteFailedDemoAsync(tenant);
            throw;
        }

        await context.StepAsync($"Pooled at https://{_containers.HostFor(slug)}", 100, ct);
    }

    private async Task<string> UniqueSlugAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var slug = DemoSlug.Mint();
            if (!await _db.Tenants.AnyAsync(t => t.Slug == slug, ct)) return slug;
        }
        throw new InvalidOperationException("Could not mint a free demo slug.");
    }

    private Task DeleteFailedDemoAsync(Tenant tenant)
    {
        // A ready container with no usable account is not pool capacity. The
        // regular provisioner owns the full container/database/log rollback.
        return _provisioner.DeleteAsync(tenant, CancellationToken.None);
    }
}

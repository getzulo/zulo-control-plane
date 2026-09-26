using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Billing;

/// <summary>
/// Shared Decide + StartPaid + swallow after stripe/bank pay. Controllers call
/// this instead of duplicating the licence gate and container start.
/// </summary>
public sealed class BillingLicenceStarter
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantContainerService _containers;
    private readonly ILogger<BillingLicenceStarter> _logger;

    public BillingLicenceStarter(
        ControlPlaneDbContext db,
        TenantContainerService containers,
        ILogger<BillingLicenceStarter> logger)
    {
        _db = db;
        _containers = containers;
        _logger = logger;
    }

    /// <summary>
    /// If the stand is Suspended for unpaid (not customer-stopped), start it.
    /// Failures are logged; the pay already succeeded and the sweep may retry.
    /// </summary>
    public async Task TryStartAfterPayAsync(string standSlug, string reason, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == standSlug, ct);
        if (tenant is null) return;

        var action = BillingLicence.Decide(
            tenant.Status,
            tenant.StoppedByCustomer,
            demo: tenant.Demo != null,
            sliceOverdue: false,
            sliceSettled: true);

        if (action != BillingLicenceAction.StartPaid) return;

        if (string.IsNullOrWhiteSpace(tenant.ContainerId))
        {
            _logger.LogWarning(
                "Pay settled {Slug} but StartPaid skipped: no container ({Reason})",
                tenant.Slug, reason);
            return;
        }

        try
        {
            await _containers.StartAsync(tenant.ContainerId!, ct);
            tenant.Status = TenantStatus.Active;
            tenant.LastError = null;
            tenant.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Started paid stand {Slug} after {Reason}",
                tenant.Slug, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Pay settled {Slug} but StartPaid failed; sweep may retry ({Reason})",
                tenant.Slug, reason);
        }
    }
}

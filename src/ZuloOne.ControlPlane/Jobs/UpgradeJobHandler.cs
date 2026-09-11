using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.Upgrade"/>.</summary>
public sealed record UpgradePayload(string ImageTag);

/// <summary>
/// Moves one tenant to another image tag.
///
/// <para>
/// A shell over <see cref="TenantUpgradeService"/>, which a fleet-wide rollout uses
/// too — the alternative was a second copy of the snapshot-recreate-gate-rollback
/// sequence, and the two would have drifted at the first fix applied to one of them.
/// </para>
///
/// <para>
/// The service RETURNS failure so a wave can decide whether to continue. This handler
/// turns that back into the exception it has always thrown, with the same text, so
/// nothing an operator reads changes.
/// </para>
/// </summary>
public sealed class UpgradeJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantUpgradeService _upgrades;

    public UpgradeJobHandler(ControlPlaneDbContext db, TenantUpgradeService upgrades)
    {
        _db = db;
        _upgrades = upgrades;
    }

    public JobKind Kind => JobKind.Upgrade;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<UpgradePayload>()
            ?? throw new InvalidOperationException("An Upgrade job must name the image tag to move to.");
        var id = context.Job.TenantId ?? throw new InvalidOperationException("An Upgrade job must name a tenant.");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");

        if (string.Equals(tenant.ImageTag, payload.ImageTag, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{tenant.Slug}' is already on {payload.ImageTag}.");

        var outcome = await _upgrades.MoveAsync(
            tenant, payload.ImageTag, setModels: false, models: null, context, 0, 100, ct);

        if (!outcome.Succeeded)
            throw new InvalidOperationException(outcome.Error ?? $"'{tenant.Slug}' could not be moved to {payload.ImageTag}.");

        // Not a failure — the tenant is serving. But an upgrade whose models do not
        // build has not delivered what it was run for, and saying so in the job log is
        // the difference between finding that now and finding it from a user.
        if (outcome.CompilationProblems.Count > 0)
        {
            await context.LogAsync(
                $"WARNING: {outcome.CompilationProblems.Count} model(s) do not compile on the new image: "
                + string.Join(" | ", outcome.CompilationProblems.Take(10)), ct);
        }
    }
}

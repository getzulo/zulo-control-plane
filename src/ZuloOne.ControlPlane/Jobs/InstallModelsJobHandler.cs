using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.InstallModels"/>.</summary>
/// <param name="ImageTag">Where to take the models FROM. Null means the tenant's own image.</param>
/// <param name="Models">Which models to install. Empty means everything the image carries.</param>
public sealed record InstallModelsPayload(string? ImageTag, string[] Models);

/// <summary>
/// Installs models into a RUNNING tenant, without recreating its container.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed, a model reached a tenant only by being baked into its image
/// and installed at boot, so adding one cost a snapshot, a container recreate and a
/// health gate — the price of a deployment for what is underneath a metadata write
/// and a compile. The panel reaches tenant HTTP already (the health probe has always
/// used it); what was missing was somewhere to send the tree and something to send.
/// </para>
///
/// <para>
/// The snapshot is still taken, and here it matters MORE rather than less. An image
/// move can be undone by pinning the previous tag back; this cannot — the container
/// never changed. The snapshot is the only way out, so it is not optional and the job
/// refuses rather than proceeding without one.
/// </para>
///
/// <para>
/// The tenant's own pin is updated to match, so the next recreate — whenever it
/// happens, for whatever reason — installs the same set rather than silently
/// reverting to what the fleet default says.
/// </para>
/// </remarks>
public sealed class InstallModelsJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly ImageTreeReader _trees;
    private readonly TenantApiClient _tenants;
    private readonly TenantUpgradeService _upgrades;
    private readonly FleetConfig _fleet;

    public InstallModelsJobHandler(
        ControlPlaneDbContext db,
        ImageTreeReader trees,
        TenantApiClient tenants,
        TenantUpgradeService upgrades,
        FleetConfig fleet)
    {
        _db = db;
        _trees = trees;
        _tenants = tenants;
        _upgrades = upgrades;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.InstallModels;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<InstallModelsPayload>()
            ?? throw new InvalidOperationException("An InstallModels job must name the models to install.");
        var id = context.Job.TenantId ?? throw new InvalidOperationException("An InstallModels job must name a tenant.");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");

        var source = string.IsNullOrWhiteSpace(payload.ImageTag) ? tenant.ImageTag : payload.ImageTag!;
        var wanted = payload.Models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        await context.StepAsync("Snapshotting — the only way back from this", 10, ct);
        var snapshot = await _upgrades.TakeSnapshotAsync(tenant, tenant.ImageTag, source, context, ct);
        await context.LogAsync(
            $"Snapshot {snapshot.FileName} ({snapshot.SizeBytes / 1024} KB). The container is not being recreated, "
            + "so there is no previous image to pin back — this file is the rollback.", ct);

        await context.StepAsync($"Reading the model tree from {source}", 30, ct);
        var tree = await _trees.ReadTreeAsync(source, wanted, ct);
        if (tree is null || tree.Length == 0)
        {
            throw new InvalidOperationException(
                $"{source} carries no model tree" + (wanted.Count > 0 ? $" for {string.Join(", ", wanted)}" : "")
                + ". A platform-only image has none; a distribution image is built with one.");
        }
        await context.LogAsync($"{tree.Length / 1024} KB of models read straight from the registry.", ct);

        await context.StepAsync($"Installing into {tenant.Slug} while it runs", 55, ct);
        var result = await _tenants.InstallTreeAsync(
            tenant, tree, TimeSpan.FromSeconds(Math.Max(_fleet.ReadinessTimeoutSeconds, 300)), ct);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"{tenant.Slug} did not install the models: {string.Join(" | ", result.Errors.Take(5))}. "
                + $"The tenant is still running what it had; snapshot {snapshot.FileName} predates the attempt.");
        }

        await context.LogAsync($"Installed: {result.Created} created, {result.Updated} updated.", ct);

        // Recorded so the next recreate installs the same set. Without this the tenant
        // would quietly revert to the fleet default the first time its container was
        // rebuilt for any unrelated reason.
        if (wanted.Count > 0)
        {
            await context.StepAsync("Recording the pin", 85, ct);
            tenant.Models = System.Text.Json.JsonSerializer.Serialize(wanted);
            await _db.SaveChangesAsync(ct);
        }

        if (result.CompilationProblems.Count > 0)
        {
            // Installed is not the same as working. The models are in place and the
            // tenant is serving; saying this plainly is the difference between finding
            // it now and hearing it from a user.
            throw new InvalidOperationException(
                $"The models installed into {tenant.Slug} but {result.CompilationProblems.Count} did not compile: "
                + string.Join(" | ", result.CompilationProblems.Take(5))
                + $". Snapshot {snapshot.FileName} predates the install.");
        }

        await context.StepAsync($"{tenant.Slug} has the models and is still serving", 100, ct);
    }
}

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
/// The snapshot is restored automatically when the install (or its compile) fails.
/// An image move can be undone by pinning the previous tag back; this cannot — the
/// container never changed. The snapshot is the only way out, so it is not optional
/// and the job refuses rather than proceeding without one.
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
    private readonly LiveSnapshotRestore _rollback;
    private readonly FleetConfig _fleet;

    public InstallModelsJobHandler(
        ControlPlaneDbContext db,
        ImageTreeReader trees,
        TenantApiClient tenants,
        TenantUpgradeService upgrades,
        LiveSnapshotRestore rollback,
        FleetConfig fleet)
    {
        _db = db;
        _trees = trees;
        _tenants = tenants;
        _upgrades = upgrades;
        _rollback = rollback;
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
        var previousPin = tenant.Models;

        var source = string.IsNullOrWhiteSpace(payload.ImageTag) ? tenant.ImageTag : payload.ImageTag!;
        var requested = payload.Models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wanted = requested;
        if (wanted.Count > 0)
        {
            var graph = (await _trees.ReadGraphAsync(source, ct))
                .ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
            wanted = ModelGraph.Expand(wanted, graph)
                .Where(name => !StandModel.IsShippedName(name) && !TestFixtureModel.IsName(name))
                .Where(name => !graph.TryGetValue(name, out var node) || !node.IsSystem)
                .ToList();
            if (wanted.Count == 0)
                throw new InvalidOperationException("TestBench and TestBenchExt are test fixtures and are not installed onto a tenant.");
        }

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

        Exception? failure = null;
        try
        {
            await context.StepAsync($"Installing into {tenant.Slug} while it runs", 55, ct);
            var result = await _tenants.InstallTreeAsync(
                tenant, tree, TimeSpan.FromSeconds(Math.Max(_fleet.ReadinessTimeoutSeconds, 300)), ct);

            if (!result.Succeeded)
            {
                failure = new InvalidOperationException(
                    $"{tenant.Slug} did not install the models: {string.Join(" | ", result.Errors.Take(5))}.");
            }
            else
            {
                await context.LogAsync($"Installed: {result.Created} created, {result.Updated} updated.", ct);
                if (result.Errors.Count > 0)
                {
                    await context.LogAsync(
                        "Import remarks (per-model; siblings that compiled are still stamped): "
                        + string.Join(" | ", result.Errors.Take(8)), ct);
                }

                if (wanted.Count > 0)
                {
                    await context.StepAsync("Recording the pin", 85, ct);
                    tenant.Models = System.Text.Json.JsonSerializer.Serialize(wanted);
                    await _db.SaveChangesAsync(ct);
                }

                var relevant = ModelGraph.ProblemsFor(result.CompilationProblems, wanted);
                if (relevant.Count > 0)
                {
                    failure = new InvalidOperationException(
                        $"The models installed into {tenant.Slug} but {relevant.Count} of the requested set did not compile: "
                        + string.Join(" | ", relevant.Take(5)) + ".");
                }
                else if (result.CompilationProblems.Count > 0)
                {
                    await context.LogAsync(
                        "Other models still do not compile (not in this install): "
                        + string.Join(" | ", result.CompilationProblems.Take(5)), ct);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            tenant.Models = previousPin;
            await _db.SaveChangesAsync(CancellationToken.None);
            try
            {
                await _rollback.RestoreAsync(tenant, snapshot, context, CancellationToken.None);
            }
            catch (Exception rollback)
            {
                throw new InvalidOperationException(
                    $"{failure.Message} Rollback of snapshot {snapshot.FileName} also failed: {rollback.Message}. "
                    + "Restore that file into a copy and swap it in.",
                    failure);
            }
            throw new InvalidOperationException(
                $"{failure.Message} Rolled back to snapshot {snapshot.FileName}.",
                failure);
        }

        await context.StepAsync($"{tenant.Slug} has the models and is still serving", 100, ct);
    }
}

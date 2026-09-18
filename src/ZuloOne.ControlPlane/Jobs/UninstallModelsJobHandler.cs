using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.UninstallModels"/>.</summary>
public sealed record UninstallModelsPayload(string Model);

/// <summary>
/// Cascade-deletes one model from a RUNNING tenant, without recreating it.
/// </summary>
/// <remarks>
/// <para>
/// Install is the counterpart and this is its inverse: a snapshot, then a
/// metadata write against live HTTP, then the pin so a later recreate does not
/// put the model back. Incoming dependents refuse; Core and the seeded stand
/// model are refused. The panel token is what lets a Zulo product model through
/// Core's write-lock.
/// </para>
/// </remarks>
public sealed class UninstallModelsJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantApiClient _tenants;
    private readonly TenantUpgradeService _upgrades;
    private readonly LiveSnapshotRestore _rollback;
    private readonly TenantModelsService _models;
    private readonly FleetConfig _fleet;

    public UninstallModelsJobHandler(
        ControlPlaneDbContext db,
        TenantApiClient tenants,
        TenantUpgradeService upgrades,
        LiveSnapshotRestore rollback,
        TenantModelsService models,
        FleetConfig fleet)
    {
        _db = db;
        _tenants = tenants;
        _upgrades = upgrades;
        _rollback = rollback;
        _models = models;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.UninstallModels;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<UninstallModelsPayload>()
            ?? throw new InvalidOperationException("An UninstallModels job must name the model to remove.");
        var id = context.Job.TenantId ?? throw new InvalidOperationException("An UninstallModels job must name a tenant.");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");

        var name = payload.Model?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Name the model to remove.");

        var (installed, readError) = await _models.ReadAsync(tenant, ct);
        if (readError is not null)
            throw new InvalidOperationException($"Could not read the models of {tenant.Slug}: {readError}");

        var row = installed.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{name}' is not installed on {tenant.Slug}.");
        if (row.IsSystem)
            throw new InvalidOperationException($"'{row.Name}' is a system model and cannot be deleted.");
        if (StandModel.IsShippedName(row.Name) || StandModel.IsMetaId(row.MetaId.ToString()))
            throw new InvalidOperationException($"'{row.Name}' is the tenant's own stand model and cannot be deleted.");

        await context.StepAsync("Snapshotting — the only way back from this", 10, ct);
        var snapshot = await _upgrades.TakeSnapshotAsync(tenant, tenant.ImageTag, tenant.ImageTag, context, ct);
        await context.LogAsync(
            $"Snapshot {snapshot.FileName} ({snapshot.SizeBytes / 1024} KB). The container is not being recreated, "
            + "so there is no previous image to pin back — this file is the rollback.", ct);

        var previousPin = tenant.Models;
        await context.StepAsync($"Removing {row.Name} from {tenant.Slug}", 45, ct);
        var timeout = TimeSpan.FromSeconds(Math.Max(_fleet.ReadinessTimeoutSeconds, 300));
        var deleted = await _tenants.DeleteModelAsync(tenant, row.MetaId, timeout, ct);
        if (!deleted.Succeeded)
        {
            if (deleted.DependentModels.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Cannot remove {row.Name}: {string.Join(", ", deleted.DependentModels)} still depend on it.");
            }
            await RollbackUninstallAsync(tenant, previousPin, snapshot, context,
                $"{tenant.Slug} did not remove {row.Name}: {deleted.Error}.");
        }
        await context.LogAsync(
            $"Removed {row.Name}: {deleted.RowsDeleted} metadata row(s), {deleted.DroppedTables.Count} table(s) dropped.", ct);

        await context.StepAsync("Recording the pin", 75, ct);
        var remaining = installed
            .Where(m => !m.IsSystem)
            .Where(m => !StandModel.IsShippedName(m.Name) && !StandModel.IsMetaId(m.MetaId.ToString()))
            .Where(m => !string.Equals(m.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        tenant.Models = JsonSerializer.Serialize(remaining);
        await _db.SaveChangesAsync(ct);

        await context.StepAsync("Materializing what remains", 85, ct);
        var materialize = await _tenants.MaterializeAsync(tenant, timeout, ct);
        if (!materialize.Succeeded)
        {
            await RollbackUninstallAsync(tenant, previousPin, snapshot, context,
                $"{row.Name} is gone from {tenant.Slug} but materialize failed: "
                + string.Join(" | ", materialize.Errors.Take(5)) + ".");
        }
        if (materialize.CompilationProblems.Count > 0)
        {
            await RollbackUninstallAsync(tenant, previousPin, snapshot, context,
                $"{row.Name} is gone from {tenant.Slug} but {materialize.CompilationProblems.Count} model(s) did not compile: "
                + string.Join(" | ", materialize.CompilationProblems.Take(5)) + ".");
        }

        await context.StepAsync($"{tenant.Slug} no longer has {row.Name} and is still serving", 100, ct);
    }

    private async Task RollbackUninstallAsync(
        Tenant tenant, string? previousPin, Snapshot snapshot, JobContext context, string reason)
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
                $"{reason} Rollback of snapshot {snapshot.FileName} also failed: {rollback.Message}. "
                + "Restore that file into a copy and swap it in.");
        }
        throw new InvalidOperationException($"{reason} Rolled back to snapshot {snapshot.FileName}.");
    }
}

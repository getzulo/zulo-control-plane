using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>What one tenant's move did, for a caller that has to decide what next.</summary>
/// <param name="Succeeded">The tenant is serving on the requested image.</param>
/// <param name="Error">Operator-facing text; null on success.</param>
/// <param name="SnapshotFile">The pre-move snapshot — the only way back past a migration.</param>
/// <param name="RolledBack">The previous tag was pinned back and the tenant is serving on it.</param>
/// <param name="CompilationProblems">
/// Models the tenant reports as not compiling AFTER the move. Deliberately not a
/// failure here: a tenant can arrive with one already broken, and failing on that
/// would roll back every future move for a reason the move did not cause. A wave
/// stops on it; a single upgrade reports it.
/// </param>
public sealed record UpgradeOutcome(
    bool Succeeded,
    string? Error,
    string? SnapshotFile,
    bool RolledBack,
    IReadOnlyList<string> CompilationProblems);

/// <summary>
/// Moves one tenant onto another image and/or another model set.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="UpgradeJobHandler"/> so a fleet-wide rollout can reuse
/// it rather than grow a second, drifting copy. The shape follows
/// <see cref="TenantProvisioner"/>: a service that does the work, a handler that is a
/// shell over it, and a <see cref="JobContext"/> that is optional throughout.
/// </para>
///
/// <para>
/// Two differences from the handler it came out of, both forced by the wave. Progress
/// is reported into a WINDOW rather than absolute percentages, because twenty tenants
/// each reporting 0→100 would reset the bar twenty times. And failure is RETURNED
/// rather than thrown, because the wave has to decide whether to stop, and an
/// exception is not a decision — the handler translates the outcome back into the
/// exception it always threw, so its operator-facing behaviour is unchanged.
/// </para>
///
/// <para>
/// Changing the model set is the same operation as changing the image: the list is
/// read at container creation, so it only takes effect on a recreate, and a recreate
/// needs the same snapshot, health gate and pin-back. "Just change the models" is not
/// a cheaper action; it is this one.
/// </para>
/// </remarks>
public sealed class TenantUpgradeService
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly TenantModelsService _models;
    private readonly PgTools _pg;
    private readonly SnapshotSettings _snapshots;
    private readonly FleetConfig _fleet;

    public TenantUpgradeService(
        ControlPlaneDbContext db,
        TenantContainerService containers,
        TenantHealthProbe health,
        TenantDatabaseProvisioner databases,
        TenantModelsService models,
        PgTools pg,
        IOptions<SnapshotSettings> snapshots,
        FleetConfig fleet)
    {
        _db = db;
        _containers = containers;
        _health = health;
        _databases = databases;
        _models = models;
        _pg = pg;
        _snapshots = snapshots.Value;
        _fleet = fleet;
    }

    /// <param name="toImageTag">Null keeps the tenant on its current image.</param>
    /// <param name="models">
    /// The new pin, when <paramref name="setModels"/> is true. Null then means "follow
    /// the fleet default" — which is a real choice and not the same as "leave alone",
    /// hence the separate flag.
    /// </param>
    public async Task<UpgradeOutcome> MoveAsync(
        Tenant tenant,
        string? toImageTag,
        bool setModels,
        string? models,
        JobContext? job,
        int progressFrom,
        int progressTo,
        CancellationToken ct)
    {
        int At(int percent) => progressFrom + (progressTo - progressFrom) * percent / 100;
        async Task Step(string text, int percent, CancellationToken token)
        {
            if (job is not null) await job.StepAsync($"{tenant.Slug}: {text}", At(percent), token);
        }
        async Task Log(string text, CancellationToken token)
        {
            if (job is not null) await job.LogAsync($"{tenant.Slug}: {text}", token);
        }

        var previousTag = tenant.ImageTag;
        var previousModels = tenant.Models;
        var targetTag = string.IsNullOrWhiteSpace(toImageTag) ? previousTag : toImageTag!;

        var tagMoves = !string.Equals(previousTag, targetTag, StringComparison.Ordinal);
        var modelsMove = setModels && !string.Equals(previousModels, models, StringComparison.Ordinal);
        if (!tagMoves && !modelsMove)
        {
            return new UpgradeOutcome(true, null, null, false, []);
        }
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName) || string.IsNullOrWhiteSpace(tenant.DatabaseRole))
        {
            return new UpgradeOutcome(false,
                $"'{tenant.Slug}' has no database — provision it before moving it.", null, false, []);
        }

        var what = tagMoves ? $"{previousTag} → {targetTag}" : "a new model set";
        Snapshot snapshot;
        try
        {
            await Step($"snapshotting before {what}", 10, ct);
            snapshot = await TakeSnapshotAsync(tenant, previousTag, targetTag, job, ct);
            await Log($"Snapshot {snapshot.FileName} ({snapshot.SizeBytes / 1024} KB) is the rollback.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpgradeOutcome(false, ex.Message, null, false, []);
        }

        await Step($"recreating on {what}", 40, ct);
        tenant.ImageTag = targetTag;
        if (setModels) tenant.Models = models;
        var connectionString = _databases.TenantConnectionString(
            tenant.DatabaseName!, tenant.DatabaseRole!, tenant.DatabasePassword ?? string.Empty);

        // The recreate AND the health gate sit inside one try.
        //
        // They did not, and the rollback was inside `if (!ready)` — so it covered
        // "started but unhealthy" and not "did not start at all". When a pull was
        // missing and CreateContainer threw NotFound, the exception went straight past
        // the rollback: the old container had already been removed, so the tenant was
        // left with NONE and served 404 until a human noticed. Measured, on a live
        // tenant.
        var ready = false;
        try
        {
            tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, ct);
            await _db.SaveChangesAsync(ct);

            await Step("waiting for it to come up", 65, ct);
            ready = await _health.WaitUntilReadyAsync(
                _containers.HostFor(tenant.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Log($"Could not bring it up on {targetTag}: {ex.Message}", CancellationToken.None);
            ready = false;
        }

        if (!ready)
        {
            return await RollBackAsync(tenant, previousTag, previousModels, setModels, targetTag,
                snapshot, connectionString, Step, ct);
        }

        tenant.Status = TenantStatus.Active;
        tenant.Health = TenantHealth.Ok;
        tenant.LastHealthAt = DateTime.UtcNow;
        tenant.LastError = null;
        await _db.SaveChangesAsync(ct);

        // Readiness proves the container serves HTTP. It does NOT prove the business
        // layer compiled — a model that fails to build leaves the tenant answering
        // /health perfectly while the thing it was moved for does not work. Read it
        // from the tenant's own database, which is the only place that knows.
        var problems = new List<string>();
        var (installed, error) = await _models.ReadAsync(tenant, ct);
        if (error is not null)
        {
            await Log($"Models could not be read after the move: {error}", ct);
        }
        else
        {
            problems.AddRange(installed
                .Where(m => !string.IsNullOrEmpty(m.CompilationStatus)
                         && !string.Equals(m.CompilationStatus, "Success", StringComparison.OrdinalIgnoreCase))
                .Select(m => $"{m.Name}: {m.CompilationError ?? m.CompilationStatus}"));
            if (problems.Count > 0)
                await Log($"{problems.Count} model(s) do not compile: {string.Join(" | ", problems.Take(5))}", ct);
        }

        await Step($"running {targetTag}", 100, ct);
        await Log("The pre-move snapshot is kept. It is the only way back past a forward-only migration.", ct);
        return new UpgradeOutcome(true, null, snapshot.FileName, false, problems);
    }

    private async Task<UpgradeOutcome> RollBackAsync(
        Tenant tenant, string previousTag, string? previousModels, bool setModels, string targetTag,
        Snapshot snapshot, string connectionString,
        Func<string, int, CancellationToken, Task> step, CancellationToken ct)
    {
        // CancellationToken.None throughout: a cancelled job must not also prevent the
        // recovery, or its record.
        await step($"did not come up — pinning back {previousTag}", 80, CancellationToken.None);
        tenant.ImageTag = previousTag;
        if (setModels) tenant.Models = previousModels;
        try
        {
            tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, CancellationToken.None);
            await _db.SaveChangesAsync(CancellationToken.None);
            var back = await _health.WaitUntilReadyAsync(
                _containers.HostFor(tenant.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), CancellationToken.None);

            tenant.Status = back ? TenantStatus.Active : TenantStatus.Failed;
            tenant.Health = back ? TenantHealth.Ok : TenantHealth.Down;
            await _db.SaveChangesAsync(CancellationToken.None);

            return new UpgradeOutcome(false, back
                ? $"{targetTag} did not come up; {previousTag} was pinned back and {tenant.Slug} is serving again. Snapshot {snapshot.FileName} was not needed."
                // Both images failing means the data has almost certainly moved under
                // the old one's feet. Say that, rather than leaving someone to work out
                // why the rollback did not help.
                : $"{targetTag} did not come up, and neither did {previousTag} afterwards — the database was probably migrated forward. Restore snapshot {snapshot.FileName} into a copy and swap it in.",
                snapshot.FileName, back, []);
        }
        catch (Exception ex)
        {
            tenant.Status = TenantStatus.Failed;
            await _db.SaveChangesAsync(CancellationToken.None);
            return new UpgradeOutcome(false,
                $"{targetTag} did not come up and {previousTag} could not be restored: {ex.Message}. Snapshot {snapshot.FileName} holds the data as it was.",
                snapshot.FileName, false, []);
        }
    }

    private async Task<Snapshot> TakeSnapshotAsync(
        Tenant tenant, string fromTag, string toTag, JobContext? job, CancellationToken ct)
    {
        Directory.CreateDirectory(_snapshots.Path);

        // The same floor SnapshotJobHandler enforces, and it was missing here — so the
        // one snapshot nobody can skip was also the one able to fill the disk that
        // floor exists to protect. Refusing costs a move that has not started yet; not
        // refusing costs the volume every other tenant's rollback lives on.
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_snapshots.Path))!);
            if (drive.AvailableFreeSpace < _snapshots.MinFreeBytes)
                throw new InvalidOperationException(
                    $"Only {drive.AvailableFreeSpace / 1024 / 1024} MB free on the snapshot volume, below the "
                  + $"{_snapshots.MinFreeBytes / 1024 / 1024} MB floor. Prune snapshots first — a move whose "
                  + "rollback cannot be written is not one.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // An unreadable drive must not block a move; the dump below fails loudly
            // enough on its own if there is genuinely no room.
            if (job is not null) await job.LogAsync($"Could not read free space on the snapshot volume: {ex.Message}", ct);
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{tenant.Slug}-{stamp}-preupgrade.dump";
        var destination = Path.Combine(_snapshots.Path, fileName);

        var credentials = !string.IsNullOrWhiteSpace(tenant.DatabasePassword)
            ? _pg.ConnectionString(tenant.DatabaseName!, tenant.DatabaseRole!, tenant.DatabasePassword!)
            : throw new InvalidOperationException(
                $"The registry holds no database password for '{tenant.Slug}', so it cannot be snapshotted — and a move without a rollback is not one.");

        var (ok, output) = await _pg.DumpAsync(credentials, destination, ct);
        if (!string.IsNullOrWhiteSpace(output) && job is not null) await job.LogAsync(output.Trim(), ct);
        if (!ok)
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            // Refuse to continue. A move whose rollback failed to be created is a move
            // with no way back, and forward-only migrations mean there is no second
            // chance to take it.
            throw new InvalidOperationException("The pre-move snapshot failed, so the move was not attempted.");
        }

        var snapshot = new Snapshot
        {
            TenantId = tenant.Id,
            TenantSlug = tenant.Slug,
            DatabaseName = tenant.DatabaseName!,
            FileName = fileName,
            SizeBytes = new FileInfo(destination).Length,
            Kind = SnapshotKind.PreUpgrade,
            Note = $"Before {fromTag} → {toTag}",
            ImageTag = fromTag,
        };
        _db.Snapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);
        return snapshot;
    }
}

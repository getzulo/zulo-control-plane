using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Puts a just-taken snapshot back under the LIVE tenant.
/// </summary>
/// <remarks>
/// Restore-to-a-copy exists because an unknown dump must be inspected before it
/// replaces production. This path is the other case: the dump was taken seconds
/// ago from this same tenant, immediately before a metadata write that failed.
/// There is nothing to inspect. Leaving the broken scripts in place is worse than
/// the brief unavailability of swapping the database back.
///
/// CancellationToken.None on the destructive steps: a cancelled job must not
/// leave the tenant with no database at all.
/// </remarks>
public sealed class LiveSnapshotRestore
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly PgTools _pg;
    private readonly SnapshotSettings _snapshots;
    private readonly FleetConfig _fleet;

    public LiveSnapshotRestore(
        ControlPlaneDbContext db,
        TenantDatabaseProvisioner databases,
        TenantContainerService containers,
        TenantHealthProbe health,
        PgTools pg,
        IOptions<SnapshotSettings> snapshots,
        FleetConfig fleet)
    {
        _db = db;
        _databases = databases;
        _containers = containers;
        _health = health;
        _pg = pg;
        _snapshots = snapshots.Value;
        _fleet = fleet;
    }

    /// <summary>
    /// Stops the tenant, replaces its database with <paramref name="snapshot"/>,
    /// starts it again. The displaced (broken) database is renamed aside, not
    /// dropped — that rename is the undo of the undo.
    /// </summary>
    public async Task RestoreAsync(Tenant tenant, Snapshot snapshot, JobContext job, CancellationToken ct)
    {
        _ = ct;
        var none = CancellationToken.None;

        if (string.IsNullOrWhiteSpace(tenant.DatabaseName)
            || string.IsNullOrWhiteSpace(tenant.DatabaseRole)
            || string.IsNullOrWhiteSpace(tenant.DatabasePassword))
        {
            throw new InvalidOperationException(
                $"'{tenant.Slug}' has no database credentials, so the snapshot cannot be loaded back.");
        }

        var file = Path.Combine(_snapshots.Path, snapshot.FileName);
        if (!File.Exists(file))
            throw new InvalidOperationException($"Snapshot file {snapshot.FileName} is missing from disk.");

        var live = tenant.DatabaseName!;
        var archive = TenantDatabaseProvisioner.ArchiveName(live);

        await job.StepAsync("Install failed — restoring the pre-change snapshot", 88, none);
        await job.LogAsync(
            $"Stopping {tenant.Slug} and replacing '{live}' from {snapshot.FileName}. "
            + $"The failed attempt is kept as '{archive}'.", none);

        if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
            await _containers.StopAsync(tenant.ContainerId!, none);

        var renamed = false;
        var created = false;
        try
        {
            await _databases.RenameDatabaseAsync(live, archive, none);
            renamed = true;
            await _databases.CreateOwnedDatabaseAsync(live, tenant.DatabaseRole!, none);
            created = true;

            var connection = _pg.ConnectionString(live, tenant.DatabaseRole!, tenant.DatabasePassword!);
            var (ok, output) = await _pg.RestoreAsync(connection, file, none);
            if (!string.IsNullOrWhiteSpace(output)) await job.LogAsync(output.Trim(), none);
            if (!ok) throw new InvalidOperationException("pg_restore failed — see the log above.");
        }
        catch (Exception ex)
        {
            await job.LogAsync($"Restore into a new database failed: {ex.Message}. Putting '{archive}' back.", none);
            await UndoRenameAsync(live, archive, renamed, created, none);
            if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
                await _containers.StartAsync(tenant.ContainerId!, none);
            throw;
        }

        if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
            await _containers.StartAsync(tenant.ContainerId!, none);

        var ready = await _health.WaitUntilReadyAsync(
            _containers.HostFor(tenant.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), none);
        if (!ready)
        {
            await job.LogAsync(
                $"{tenant.Slug} did not come up on the restored snapshot. Putting '{archive}' back.", none);
            if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
                await _containers.StopAsync(tenant.ContainerId!, none);
            await UndoRenameAsync(live, archive, renamed: true, created: true, none);
            if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
                await _containers.StartAsync(tenant.ContainerId!, none);
            throw new TimeoutException(
                $"{tenant.Slug} did not come up after rolling back to {snapshot.FileName}. "
                + $"The failed attempt is '{archive}'.");
        }

        tenant.PreviousDatabaseName = archive;
        tenant.PreviousDatabaseAt = DateTime.UtcNow;
        tenant.Health = TenantHealth.Ok;
        tenant.LastHealthAt = DateTime.UtcNow;
        tenant.LastError = null;
        await _db.SaveChangesAsync(none);

        await job.LogAsync(
            $"Rolled back to {snapshot.FileName}. {tenant.Slug} is serving the pre-change database. "
            + $"The failed attempt is kept as '{archive}' until you discard it.", none);
    }

    private async Task UndoRenameAsync(string live, string archive, bool renamed, bool created, CancellationToken ct)
    {
        if (created)
        {
            try { await _databases.DropDatabaseAsync(live, ct); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not drop the half-restored '{live}' to put '{archive}' back: {ex.Message}. "
                    + $"Recover with: DROP DATABASE \"{live}\"; ALTER DATABASE \"{archive}\" RENAME TO \"{live}\".",
                    ex);
            }
        }
        if (renamed)
        {
            try { await _databases.RenameDatabaseAsync(archive, live, ct); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"'{live}' currently does not exist; it is present as '{archive}'. "
                    + $"Recover with: ALTER DATABASE \"{archive}\" RENAME TO \"{live}\". Cause: {ex.Message}",
                    ex);
            }
        }
    }
}

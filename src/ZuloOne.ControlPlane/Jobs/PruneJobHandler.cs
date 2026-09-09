using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Settings;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// Keeps the snapshot directory from growing without bound, and keeps the
/// registry honest about what is on the disk.
/// </summary>
/// <remarks>
/// <para>
/// Every upgrade takes a snapshot first — unconditionally, because that snapshot
/// IS the rollback — and nothing ever removed one. Measured before this existed:
/// ten dumps of a single tenant from one afternoon's work, and no code anywhere
/// that so much as listed the directory.
/// </para>
///
/// <para>
/// It runs as a job so it inherits <see cref="JobWorker"/>'s single-file queue.
/// That is not tidiness: a sweep on its own timer would eventually delete a file
/// while <c>RestoreJobHandler</c> was reading it, and the failure would look like
/// a corrupt dump rather than a race.
/// </para>
/// </remarks>
public sealed class PruneJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly SettingsStore _settings;
    private readonly SnapshotSettings _paths;

    public PruneJobHandler(
        ControlPlaneDbContext db,
        SettingsStore settings,
        Microsoft.Extensions.Options.IOptions<SnapshotSettings> paths)
    {
        _db = db;
        _settings = settings;
        _paths = paths.Value;
    }

    public JobKind Kind => JobKind.Prune;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var keepPerTenant = _settings.Int("Snapshots:KeepPerTenant");
        var keepDays = _settings.Int("Snapshots:KeepDays");
        var graceHours = _settings.Int("Snapshots:OrphanGraceHours");
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);

        await context.StepAsync($"Keeping {keepPerTenant} per tenant and everything under {keepDays} days", 10, ct);

        var all = await _db.Snapshots.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);

        // A restored copy is built FROM a snapshot and an operator is still
        // deciding whether to swap it in. Deleting the thing it came from would
        // remove the only way to explain what they are looking at.
        var inUse = await _db.Tenants
            .Where(t => t.RestoredFromSnapshotId != null)
            .Select(t => t.RestoredFromSnapshotId!.Value)
            .ToListAsync(ct);

        var doomed = new List<Snapshot>();
        var keptBecauseInUse = 0;

        foreach (var group in all.Where(s => s.Kind != SnapshotKind.Manual).GroupBy(s => s.TenantSlug))
        {
            // Newest first, so Skip() keeps the newest N.
            var ordered = group.OrderByDescending(s => s.CreatedAt).ToList();
            foreach (var snapshot in ordered.Skip(keepPerTenant))
            {
                // The age rule is a floor UNDER the count rule, not an alternative
                // to it: a run of upgrades in one afternoon must not evict
                // yesterday's rollback point just by being more numerous.
                if (snapshot.CreatedAt >= cutoff) continue;
                if (inUse.Contains(snapshot.Id)) { keptBecauseInUse++; continue; }
                doomed.Add(snapshot);
            }
        }

        var manual = all.Count(s => s.Kind == SnapshotKind.Manual);
        await context.LogAsync(
            $"{all.Count} snapshot(s): {manual} manual (never swept), {doomed.Count} past the policy, "
            + $"{keptBecauseInUse} held by a restored copy.", ct);

        await context.StepAsync($"Removing {doomed.Count} snapshot(s)", 40, ct);
        long freed = 0;
        foreach (var snapshot in doomed)
        {
            var path = Path.Combine(_paths.Path, snapshot.FileName);
            try
            {
                if (File.Exists(path)) { freed += new FileInfo(path).Length; File.Delete(path); }
            }
            catch (Exception ex)
            {
                // The row stays if the file would not go, so the next sweep tries
                // again rather than leaving a row pointing at a file that is still
                // there and still counted.
                await context.LogAsync($"Could not delete {snapshot.FileName}: {ex.Message}", ct);
                continue;
            }
            _db.Snapshots.Remove(snapshot);
        }
        await _db.SaveChangesAsync(ct);

        await context.StepAsync("Reconciling the directory against the registry", 70, ct);
        var (orphanFiles, orphanBytes, missingFiles) = await ReconcileAsync(all, doomed, graceHours, context, ct);

        var free = FreeBytes();
        await context.StepAsync(
            $"Freed {(freed + orphanBytes) / 1024 / 1024} MB — {doomed.Count} expired, {orphanFiles} orphaned, "
            + $"{missingFiles} row(s) with no file. {free / 1024 / 1024} MB free.",
            100, ct);
    }

    /// <summary>
    /// Both directions. Nothing in the panel had ever listed this directory, so a
    /// file with no row was invisible to the entire system and accumulated for
    /// ever — and <c>TenantProvisioner.DeleteAsync</c> guarantees them: it drops
    /// the container, volume, database, role and registry row, and leaves the
    /// snapshots.
    /// </summary>
    private async Task<(int OrphanFiles, long OrphanBytes, int MissingFiles)> ReconcileAsync(
        List<Snapshot> before, List<Snapshot> removed, int graceHours, JobContext context, CancellationToken ct)
    {
        if (!Directory.Exists(_paths.Path)) return (0, 0, 0);

        var known = before.Except(removed).Select(s => s.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var grace = DateTime.UtcNow.AddHours(-graceHours);

        var orphans = 0;
        long bytes = 0;
        foreach (var file in Directory.GetFiles(_paths.Path, "*.dump"))
        {
            if (known.Contains(Path.GetFileName(file))) continue;

            // A dump in progress looks exactly like an orphan — same directory,
            // same extension, no row until it finishes. The grace period is the
            // only thing separating the two.
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc > grace) continue;

            try { bytes += info.Length; info.Delete(); orphans++; }
            catch (Exception ex) { await context.LogAsync($"Orphan {info.Name}: {ex.Message}", ct); }
        }
        if (orphans > 0) await context.LogAsync($"Removed {orphans} file(s) with no registry row.", ct);

        // The opposite: a row whose file is gone. Left in place — the Backups
        // screen already shows it as "file missing", and that is information an
        // operator wants rather than a row silently vanishing. Counted so the
        // number is visible in the job log.
        var missing = before.Except(removed)
            .Count(s => !File.Exists(Path.Combine(_paths.Path, s.FileName)));
        if (missing > 0)
            await context.LogAsync($"{missing} row(s) point at a file that is gone — shown as 'file missing'.", ct);

        return (orphans, bytes, missing);
    }

    private long FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_paths.Path))!).AvailableFreeSpace; }
        catch { return 0; }
    }
}

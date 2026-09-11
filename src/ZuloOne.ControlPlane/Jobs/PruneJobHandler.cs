using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Api;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Settings;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// Keeps the snapshot directory from growing without bound, keeps the registry
/// honest about what is on the disk, and keeps the app host from hoarding every
/// image the fleet has ever run.
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
    private readonly TenantContainerService _containers;
    private readonly FleetConfig _fleet;

    public PruneJobHandler(
        ControlPlaneDbContext db,
        SettingsStore settings,
        Microsoft.Extensions.Options.IOptions<SnapshotSettings> paths,
        TenantContainerService containers,
        FleetConfig fleet)
    {
        _db = db;
        _settings = settings;
        _paths = paths.Value;
        _containers = containers;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.Prune;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var keepPerTenant = _settings.Int("Snapshots:KeepPerTenant");
        var keepDays = _settings.Int("Snapshots:KeepDays");
        var registryDays = _settings.Int("Snapshots:RegistryKeepDays");
        var graceHours = _settings.Int("Snapshots:OrphanGraceHours");
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        var registryCutoff = DateTime.UtcNow.AddDays(-registryDays);

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

        foreach (var group in all.Where(s => s.Kind != SnapshotKind.Manual && s.Kind != SnapshotKind.Registry)
                                 .GroupBy(s => s.TenantSlug))
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

        // The registry has its own rule: it belongs to no tenant, so the
        // per-tenant count is meaningless for it, and it is small enough that
        // depth is cheap. Age alone, and always keep the newest — a fleet with no
        // recorded registry at all is the state this exists to prevent.
        var registry = all.Where(s => s.Kind == SnapshotKind.Registry)
            .OrderByDescending(s => s.CreatedAt).ToList();
        var expiredRegistry = registry.Skip(1).Where(s => s.CreatedAt < registryCutoff).ToList();
        doomed.AddRange(expiredRegistry);

        await context.LogAsync(
            $"{all.Count} snapshot(s): {manual} manual (never swept), {registry.Count} registry "
            + $"({expiredRegistry.Count} past {registryDays} days), {doomed.Count} to remove, "
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

        await context.StepAsync("Sweeping images the fleet no longer runs", 85, ct);
        var (imagesRemoved, imageBytes) = await SweepImagesAsync(context, ct);

        var free = FreeBytes();
        await context.StepAsync(
            $"Freed {(freed + orphanBytes) / 1024 / 1024} MB — {doomed.Count} expired, {orphanFiles} orphaned, "
            + $"{missingFiles} row(s) with no file. {imagesRemoved} image(s) "
            + $"({imageBytes / 1024 / 1024} MB) off the app host. {free / 1024 / 1024} MB free.",
            100, ct);
    }

    /// <summary>
    /// Old tenant images on the app host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing else removes them. <c>TenantContainerService.EnsureImageAsync</c>
    /// PULLS on every provision and every upgrade and there has never been a call
    /// that deletes one, so the app host gains roughly an image per upgrade — on a
    /// 120 GB disk it shares with every tenant container.
    /// </para>
    ///
    /// <para>
    /// Kept: anything a tenant row names, the fleet default, and the newest
    /// <c>Images:KeepRecent</c> whatever their state. That last one is the rollback
    /// window — <c>TenantUpgradeService.MoveAsync</c> rolls a tenant back by moving
    /// it to its PREVIOUS tag, which needs that image to still be here.
    /// </para>
    ///
    /// <para>
    /// Count, not age, and the difference matters. Snapshots expire by age because
    /// a dump's timestamp is when it was taken; an image's is when it was BUILT.
    /// A tenant twenty builds behind would have its rollback target swept the
    /// instant it upgraded — the image is old, the need for it is one minute old.
    /// Counting survives that; ageing does not.
    /// </para>
    ///
    /// <para>
    /// Falling outside the window is not a dead end either. The registry keeps
    /// every release precisely so it is not — <c>ImagesController.WhyUndeletable</c>
    /// refuses to delete one — and it is on the LAN, so rolling back past the window
    /// costs a pull rather than being impossible.
    /// </para>
    /// </remarks>
    private async Task<(int Removed, long Bytes)> SweepImagesAsync(JobContext context, CancellationToken ct)
    {
        var keepRecent = _settings.Int("Images:KeepRecent");

        // One definition of how an image name splits, shared with the panel and the
        // models catalogue. Recombined because RepoTags carry the registry host and
        // SplitImage hands it back separately.
        var (registry, repository) = ImagesController.SplitImage(_fleet.DefaultImage);
        var repo = registry is null ? repository : $"{registry}/{repository}";

        List<DaemonImage> images;
        try
        {
            images = (await _containers.ListImagesAsync(repo, ct)).ToList();
        }
        catch (Exception ex)
        {
            // The app host being unreachable is a real condition and not this job's
            // to solve. Snapshots were already swept above; say so and stop.
            await context.LogAsync($"Could not list images on the app host: {ex.Message}", ct);
            return (0, 0);
        }

        // A tenant row names the image even when its container is gone — mid
        // re-provision, or suspended. The row is the authority, not the daemon.
        var pinned = await _db.Tenants.AsNoTracking()
            .Select(t => t.ImageTag)
            .Distinct()
            .ToListAsync(ct);

        var keep = new HashSet<string>(pinned.Where(t => !string.IsNullOrWhiteSpace(t))!, StringComparer.Ordinal)
        {
            _fleet.DefaultImage,
        };

        var removed = 0;
        long bytes = 0;
        var held = 0;
        foreach (var image in images.Skip(keepRecent))
        {
            if (image.Tags.Any(keep.Contains)) { held++; continue; }
            if (await _containers.RemoveImageAsync(image.Id, ct))
            {
                removed++;
                bytes += image.Size;
            }
            else
            {
                // Refused, which means a container still references it — a tenant
                // whose row was deleted but whose container outlived it, most likely.
                held++;
            }
        }

        await context.LogAsync(
            $"{images.Count} image(s) of {repo} on the app host: {Math.Min(keepRecent, images.Count)} newest kept, "
            + $"{held} still referenced, {removed} removed ({bytes / 1024 / 1024} MB).", ct);

        return (removed, bytes);
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

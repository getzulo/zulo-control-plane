using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.Upgrade"/>.</summary>
public sealed record UpgradePayload(string ImageTag);

/// <summary>
/// Moves one tenant to another image tag.
///
/// <para>
/// A snapshot is taken FIRST and it is the rollback — §8 of the deployment design
/// says so, and it is the only thing that can undo a forward-only migration. The
/// container is then recreated on the new tag and health-gated; first boot runs
/// migrations, schema sync and a metadata compile against the tenant's real data,
/// which is where an incompatible image actually fails.
/// </para>
///
/// <para>
/// On failure the OLD tag is pinned back automatically. In the common case —
/// the image does not start at all — no migration ran and the tenant simply
/// returns. When that does not work either, the job says so plainly and names the
/// snapshot, because at that point a human has to choose between restoring data
/// and chasing the image.
/// </para>
/// </summary>
public sealed class UpgradeJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly PgTools _pg;
    private readonly SnapshotSettings _snapshots;
    private readonly FleetSettings _fleet;

    public UpgradeJobHandler(
        ControlPlaneDbContext db,
        TenantContainerService containers,
        TenantHealthProbe health,
        TenantDatabaseProvisioner databases,
        PgTools pg,
        IOptions<SnapshotSettings> snapshots,
        IOptions<FleetSettings> fleet)
    {
        _db = db;
        _containers = containers;
        _health = health;
        _databases = databases;
        _pg = pg;
        _snapshots = snapshots.Value;
        _fleet = fleet.Value;
    }

    public JobKind Kind => JobKind.Upgrade;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<UpgradePayload>()
            ?? throw new InvalidOperationException("An Upgrade job must name the image tag to move to.");
        var id = context.Job.TenantId ?? throw new InvalidOperationException("An Upgrade job must name a tenant.");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");

        var previousTag = tenant.ImageTag;
        if (string.Equals(previousTag, payload.ImageTag, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{tenant.Slug}' is already on {payload.ImageTag}.");
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName) || string.IsNullOrWhiteSpace(tenant.DatabaseRole))
            throw new InvalidOperationException($"'{tenant.Slug}' has no database — provision it before upgrading.");

        await context.StepAsync($"Snapshotting before {previousTag} → {payload.ImageTag}", 10, ct);
        var snapshot = await TakeSnapshotAsync(tenant, previousTag, payload.ImageTag, context, ct);
        await context.LogAsync($"Snapshot {snapshot.FileName} ({snapshot.SizeBytes / 1024} KB) is the rollback.", ct);

        await context.StepAsync($"Recreating on {payload.ImageTag}", 40, ct);
        tenant.ImageTag = payload.ImageTag;
        var connectionString = _databases.TenantConnectionString(
            tenant.DatabaseName!, tenant.DatabaseRole!, tenant.DatabasePassword ?? string.Empty);

        // The recreate AND the health gate are inside one try.
        //
        // They were not, and the rollback sat inside `if (!ready)` — so it covered
        // "started but unhealthy" and not "did not start at all". When a pull was
        // missing and CreateContainer threw NotFound, the exception went straight
        // past the rollback to the worker: RunAsync had already removed the old
        // container, so the tenant was left with NONE and served 404 until a human
        // noticed. Measured, on a live tenant.
        var ready = false;
        try
        {
            // RunAsync removes a container of the same name first, so this replaces
            // the running one rather than colliding with it.
            tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, ct);
            await _db.SaveChangesAsync(ct);

            await context.StepAsync($"Waiting for {tenant.Slug} on the new image", 65, ct);
            ready = await _health.WaitUntilReadyAsync(
                _containers.HostFor(tenant.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await context.LogAsync($"Could not bring {tenant.Slug} up on {payload.ImageTag}: {ex.Message}", CancellationToken.None);
            ready = false;
        }

        if (!ready)
        {
            await context.StepAsync($"Did not come up — pinning back {previousTag}", 80, CancellationToken.None);
            tenant.ImageTag = previousTag;
            try
            {
                tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, CancellationToken.None);
                await _db.SaveChangesAsync(CancellationToken.None);
                var back = await _health.WaitUntilReadyAsync(
                    _containers.HostFor(tenant.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), CancellationToken.None);

                tenant.Status = back ? TenantStatus.Active : TenantStatus.Failed;
                tenant.Health = back ? TenantHealth.Ok : TenantHealth.Down;
                await _db.SaveChangesAsync(CancellationToken.None);

                throw new InvalidOperationException(back
                    ? $"{payload.ImageTag} did not come up; {previousTag} was pinned back and {tenant.Slug} is serving again. Snapshot {snapshot.FileName} was not needed."
                    // Both images failing means the data has almost certainly moved
                    // under the old one's feet. Say that, rather than leaving someone
                    // to work out why the rollback did not help.
                    : $"{payload.ImageTag} did not come up, and neither did {previousTag} afterwards — the database was probably migrated forward. Restore snapshot {snapshot.FileName} into a copy and swap it in.");
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                tenant.Status = TenantStatus.Failed;
                await _db.SaveChangesAsync(CancellationToken.None);
                throw new InvalidOperationException(
                    $"{payload.ImageTag} did not come up and {previousTag} could not be restored: {ex.Message}. Snapshot {snapshot.FileName} holds the data as it was.", ex);
            }
        }

        tenant.Status = TenantStatus.Active;
        tenant.Health = TenantHealth.Ok;
        tenant.LastHealthAt = DateTime.UtcNow;
        tenant.LastError = null;
        await _db.SaveChangesAsync(ct);

        await context.StepAsync($"{tenant.Slug} is running {payload.ImageTag}", 100, ct);
        await context.LogAsync(
            $"The pre-upgrade snapshot is kept. It is the only way back past a forward-only migration.", ct);
    }

    private async Task<Snapshot> TakeSnapshotAsync(
        Tenant tenant, string fromTag, string toTag, JobContext context, CancellationToken ct)
    {
        Directory.CreateDirectory(_snapshots.Path);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{tenant.Slug}-{stamp}-preupgrade.dump";
        var destination = Path.Combine(_snapshots.Path, fileName);

        var credentials = !string.IsNullOrWhiteSpace(tenant.DatabasePassword)
            ? _pg.ConnectionString(tenant.DatabaseName!, tenant.DatabaseRole!, tenant.DatabasePassword!)
            : throw new InvalidOperationException(
                $"The registry holds no database password for '{tenant.Slug}', so it cannot be snapshotted — and an upgrade without a rollback is not one.");

        var (ok, output) = await _pg.DumpAsync(credentials, destination, ct);
        if (!string.IsNullOrWhiteSpace(output)) await context.LogAsync(output.Trim(), ct);
        if (!ok)
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            // Refuse to continue. An upgrade whose rollback failed to be created is
            // an upgrade with no way back, and forward-only migrations mean there is
            // no second chance to take it.
            throw new InvalidOperationException("The pre-upgrade snapshot failed, so the upgrade was not attempted.");
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

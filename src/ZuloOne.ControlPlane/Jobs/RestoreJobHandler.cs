using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.Restore"/>.</summary>
public sealed record RestorePayload(Guid SnapshotId);

/// <summary>
/// Rebuilds a snapshot into a THROWAWAY tenant so it can be looked at before
/// anything irreversible happens.
///
/// <para>
/// The copy gets a random slug of its own, its own database, its own role, its own
/// container and its own hostname. It is not a replacement for the live tenant and
/// is never meant to become one: once an operator has confirmed the data is what
/// they expected, a separate <see cref="JobKind.Swap"/> moves the restored database
/// under the live tenant and the copy is discarded.
/// </para>
///
/// <para>
/// Splitting it this way is the whole point. A restore that went straight over the
/// live tenant would be a single irreversible action taken on faith that the backup
/// contains what somebody hopes it contains.
/// </para>
/// </summary>
public sealed class RestoreJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantCloneService _clone;
    private readonly SnapshotSettings _snapshots;
    private readonly FleetConfig _fleet;

    public RestoreJobHandler(
        ControlPlaneDbContext db,
        TenantCloneService clone,
        IOptions<SnapshotSettings> snapshots,
        FleetConfig fleet)
    {
        _db = db;
        _clone = clone;
        _snapshots = snapshots.Value;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.Restore;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<RestorePayload>()
            ?? throw new InvalidOperationException("A Restore job must name the snapshot to restore.");

        var snapshot = await _db.Snapshots.FirstOrDefaultAsync(s => s.Id == payload.SnapshotId, ct)
            ?? throw new InvalidOperationException("That snapshot is no longer in the registry.");

        var file = Path.Combine(_snapshots.Path, snapshot.FileName);
        if (!File.Exists(file))
            throw new InvalidOperationException($"The snapshot file {snapshot.FileName} is missing from disk.");

        // The image the ORIGINAL ran on, not today's default. A dump restored into a
        // newer image migrates forward on first boot, which is fine; restored into an
        // OLDER one it meets a schema from the future and fails in ways that read as
        // corruption. Using the recorded tag keeps the copy faithful.
        var imageTag = string.IsNullOrWhiteSpace(snapshot.ImageTag) ? _fleet.DefaultImage : snapshot.ImageTag!;

        // …and the same model pin, for the same reason. The copy exists to be compared
        // against the original before anything irreversible; a copy that installs a
        // different business layer is not the thing being compared. Null inherits
        // null, so a tenant following the fleet default keeps following it.
        var sourceModels = await _db.Tenants.AsNoTracking()
            .Where(t => t.Slug == snapshot.TenantSlug)
            .Select(t => t.Models)
            .FirstOrDefaultAsync(ct);

        // Random, because the copy is disposable and naming it after the original
        // invites someone to mistake one for the other — but prefixed, so that a
        // stray container is recognisable for what it is months later.
        var slug = await _clone.UniqueSlugAsync("restore", ct);
        await context.StepAsync($"Creating the scratch tenant {slug}", 5, ct);
        await context.LogAsync(
            $"Restoring {snapshot.TenantSlug} as of {snapshot.CreatedAt:u} on image {imageTag}.", ct);

        var tenant = new Tenant
        {
            Slug = slug,
            DisplayName = $"Restore of {snapshot.TenantSlug} ({snapshot.CreatedAt:yyyy-MM-dd HH:mm} UTC)",
            ImageTag = imageTag,
            Models = sourceModels,
            RestoredFromSlug = snapshot.TenantSlug,
            RestoredFromSnapshotId = snapshot.Id,
            RestoredAt = DateTime.UtcNow,
        };

        await _clone.CloneAsync(context, tenant, file, snapshot.SizeBytes, "scratch tenant", ct);

        await context.LogAsync(
            "Sign in and confirm the data is what you expected. Nothing has touched the live tenant. " +
            "When satisfied, swap it in; otherwise discard this copy.", ct);
    }
}

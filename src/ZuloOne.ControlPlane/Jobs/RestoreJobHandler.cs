using System.Security.Cryptography;
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
    // Deliberately unambiguous characters. This name ends up in a hostname an
    // operator reads off a screen and types, usually while something is wrong, and
    // 0/O and 1/l are how that goes wrong.
    private const string SlugAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private readonly ControlPlaneDbContext _db;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly PgTools _pg;
    private readonly SnapshotSettings _snapshots;
    private readonly FleetConfig _fleet;

    public RestoreJobHandler(
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

        var slug = await UniqueSlugAsync(ct);
        await context.StepAsync($"Creating the scratch tenant {slug}", 5, ct);
        await context.LogAsync(
            $"Restoring {snapshot.TenantSlug} as of {snapshot.CreatedAt:u} on image {imageTag}.", ct);

        var tenant = new Tenant
        {
            Slug = slug,
            DisplayName = $"Restore of {snapshot.TenantSlug} ({snapshot.CreatedAt:yyyy-MM-dd HH:mm} UTC)",
            Status = TenantStatus.Provisioning,
            ImageTag = imageTag,
            // A KEY OF ITS OWN, never the original's. A token minted in the copy must
            // not be valid against the live tenant: the copy exists to be poked at,
            // often by more people than usually touch production, and a shared key
            // would make compromising the scratch copy equivalent to compromising the
            // real thing.
            JwtSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            RestoredFromSlug = snapshot.TenantSlug,
            RestoredFromSnapshotId = snapshot.Id,
            RestoredAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        try
        {
            await context.StepAsync("Creating the database", 15, ct);
            var (database, role, password, connectionString) = await _databases.CreateAsync(slug, ct);
            tenant.DatabaseName = database;
            tenant.DatabaseRole = role;
            tenant.DatabasePassword = password;
            await _db.SaveChangesAsync(ct);

            await context.StepAsync($"Loading {snapshot.SizeBytes / 1024} KB into {database}", 30, ct);
            // As the NEW role, so every restored object is owned by it from the
            // start. Loading as the admin role would leave the tenant unable to
            // alter its own tables on first boot.
            var (ok, output) = await _pg.RestoreAsync(_pg.ConnectionString(database, role, password), file, ct);
            if (!string.IsNullOrWhiteSpace(output)) await context.LogAsync(output.Trim(), ct);
            if (!ok) throw new InvalidOperationException("pg_restore failed — see the log above.");

            await context.StepAsync("Starting the container", 60, ct);
            tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, ct);
            await _db.SaveChangesAsync(ct);

            // THE quick check. First boot runs migrations against the restored data,
            // so a dump that cannot carry the tenant forward fails HERE, on a copy,
            // rather than after it has replaced the live database.
            await context.StepAsync($"Waiting for it to answer (up to {_fleet.ReadinessTimeoutSeconds}s)", 75, ct);
            var ready = await _health.WaitUntilReadyAsync(
                _containers.HostFor(slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
            if (!ready)
                throw new TimeoutException(
                    $"The restored copy never reported ready. The data loaded, but this image could not boot on it — do NOT swap it in.");

            tenant.Status = TenantStatus.Active;
            tenant.Health = TenantHealth.Ok;
            tenant.LastHealthAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await context.StepAsync($"Ready at https://{_containers.HostFor(slug)}", 100, ct);
            await context.LogAsync(
                "Sign in and confirm the data is what you expected. Nothing has touched the live tenant. " +
                "When satisfied, swap it in; otherwise discard this copy.", ct);
        }
        catch
        {
            // CancellationToken.None: the scratch tenant must not survive its own
            // failed creation just because the caller went away.
            tenant.Status = TenantStatus.Failed;
            await _db.SaveChangesAsync(CancellationToken.None);
            await context.LogAsync("Rolling back the scratch tenant.", CancellationToken.None);
            try
            {
                if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
                    await _containers.RemoveAsync(tenant.ContainerId!, CancellationToken.None);
                await _databases.DropAsync(tenant.DatabaseName, tenant.DatabaseRole, CancellationToken.None);
                _db.Tenants.Remove(tenant);
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                await context.LogAsync($"Rollback was incomplete: {cleanup.Message}", CancellationToken.None);
            }
            throw;
        }
    }

    /// <summary>
    /// <c>restore-xxxxxx</c>. Random, because the copy is disposable and naming it
    /// after the original invites someone to mistake one for the other — but
    /// prefixed, so that a stray container is recognisable for what it is months
    /// later.
    /// </summary>
    private async Task<string> UniqueSlugAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var suffix = string.Concat(Enumerable.Range(0, 6)
                .Select(_ => SlugAlphabet[RandomNumberGenerator.GetInt32(SlugAlphabet.Length)]));
            var slug = $"restore-{suffix}";
            if (!await _db.Tenants.AnyAsync(t => t.Slug == slug, ct)) return slug;
        }
        throw new InvalidOperationException("Could not find a free slug for the restored copy.");
    }
}

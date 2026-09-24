using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Turns a snapshot file into a running tenant: its own database, its own role,
/// its own log database, the dump loaded as that role, a container, and a wait
/// until the thing actually answers — or a full rollback if any of it fails.
///
/// <para>
/// Extracted from <c>RestoreJobHandler</c>, which was the only caller when this
/// sequence was written. A demo tenant is the same nine steps against a blessed
/// template snapshot instead of an operator's backup, and duplicating them would
/// mean two copies of the rollback — the part that is hardest to get right and
/// least likely to be exercised.
/// </para>
///
/// <para>
/// WHAT THIS DOES NOT DECIDE: who the tenant is. The caller builds the
/// <see cref="Tenant"/> — slug, display name, image tag, model pin and whatever
/// provenance it wants to stamp — and this only makes it real. Restore records
/// where the copy came from; a demo records when it expires; neither concern
/// belongs here.
/// </para>
/// </summary>
public sealed class TenantCloneService
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly TenantLogDatabaseProvisioner _logDatabases;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly PgTools _pg;
    private readonly FleetConfig _fleet;

    public TenantCloneService(
        ControlPlaneDbContext db,
        TenantDatabaseProvisioner databases,
        TenantLogDatabaseProvisioner logDatabases,
        TenantContainerService containers,
        TenantHealthProbe health,
        PgTools pg,
        FleetConfig fleet)
    {
        _db = db;
        _databases = databases;
        _logDatabases = logDatabases;
        _containers = containers;
        _health = health;
        _pg = pg;
        _fleet = fleet;
    }

    /// <summary>
    /// Registers <paramref name="tenant"/> and builds it from
    /// <paramref name="snapshotFile"/>. Returns with the tenant Active and
    /// answering; throws with everything it created already removed.
    /// </summary>
    /// <param name="rollbackNoun">
    /// What the rollback line calls this tenant. The operator reading the log of a
    /// failed restore is looking for "scratch tenant", not a generic word.
    /// </param>
    public async Task CloneAsync(
        JobContext context,
        Tenant tenant,
        string snapshotFile,
        long snapshotSizeBytes,
        string rollbackNoun,
        CancellationToken ct,
        ContainerLimits? limits = null)
    {
        // A KEY OF ITS OWN, never the source's. A token minted in the copy must
        // not be valid against the tenant it was cloned from: the copy exists to
        // be poked at, often by more people than usually touch production, and a
        // shared key would make compromising the copy equivalent to compromising
        // the real thing. Defaulted here rather than trusted to each caller —
        // this is the kind of invariant that is silently dropped when a second
        // call site is added.
        if (string.IsNullOrWhiteSpace(tenant.JwtSigningKey))
            tenant.JwtSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        tenant.Status = TenantStatus.Provisioning;

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        try
        {
            await context.StepAsync("Creating the database", 15, ct);
            var (database, role, password, connectionString) = await _databases.CreateAsync(tenant.Slug, ct);
            tenant.DatabaseName = database;
            tenant.DatabaseRole = role;
            tenant.DatabasePassword = password;
            await _db.SaveChangesAsync(ct);

            var logDatabase = await _logDatabases.CreateAsync(tenant.Slug, ct);
            if (logDatabase is not null)
            {
                tenant.LogDatabase = logDatabase.Database;
                tenant.LogUser = logDatabase.User;
                tenant.LogPassword = logDatabase.Password;
                await _db.SaveChangesAsync(ct);
            }

            await context.StepAsync($"Loading {snapshotSizeBytes / 1024} KB into {database}", 30, ct);
            // As the NEW role, so every restored object is owned by it from the
            // start. Loading as the admin role would leave the tenant unable to
            // alter its own tables on first boot.
            var (ok, output) = await _pg.RestoreAsync(_pg.ConnectionString(database, role, password), snapshotFile, ct);
            if (!string.IsNullOrWhiteSpace(output)) await context.LogAsync(output.Trim(), ct);
            if (!ok) throw new InvalidOperationException("pg_restore failed — see the log above.");

            await context.StepAsync("Starting the container", 60, ct);
            tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, limits, ct);
            await _db.SaveChangesAsync(ct);

            // THE quick check. First boot runs migrations against the restored data,
            // so a dump that cannot carry the tenant forward fails HERE, on a copy,
            // rather than after it has replaced the live database.
            await context.StepAsync($"Waiting for it to answer (up to {_fleet.ReadinessTimeoutSeconds}s)", 75, ct);
            var ready = await _health.WaitUntilReadyAsync(
                _containers.HostFor(tenant.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
            if (!ready)
                throw new TimeoutException(
                    $"The restored copy never reported ready. The data loaded, but this image could not boot on it — do NOT swap it in.");

            tenant.Status = TenantStatus.Active;
            tenant.Health = TenantHealth.Ok;
            tenant.LastHealthAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await context.StepAsync($"Ready at https://{_containers.HostFor(tenant.Slug)}", 100, ct);
        }
        catch
        {
            // CancellationToken.None: the tenant must not survive its own failed
            // creation just because the caller went away.
            tenant.Status = TenantStatus.Failed;
            await _db.SaveChangesAsync(CancellationToken.None);
            await context.LogAsync($"Rolling back the {rollbackNoun}.", CancellationToken.None);
            try
            {
                if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
                    await _containers.RemoveAsync(tenant.ContainerId!, CancellationToken.None);
                await _logDatabases.DropAsync(tenant.LogDatabase, tenant.LogUser, CancellationToken.None);
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
    /// A free slug of the form <c>{prefix}-xxxxxx</c>.
    ///
    /// <para>
    /// Deliberately unambiguous characters: this name ends up in a hostname an
    /// operator reads off a screen and types, usually while something is wrong,
    /// and 0/O and 1/l are how that goes wrong.
    /// </para>
    /// </summary>
    public async Task<string> UniqueSlugAsync(string prefix, CancellationToken ct)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var suffix = string.Concat(Enumerable.Range(0, 6)
                .Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
            var slug = $"{prefix}-{suffix}";
            if (!await _db.Tenants.AnyAsync(t => t.Slug == slug, ct)) return slug;
        }
        throw new InvalidOperationException($"Could not find a free slug with the prefix {prefix}.");
    }
}

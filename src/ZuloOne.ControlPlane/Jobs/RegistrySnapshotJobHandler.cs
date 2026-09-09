using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// Dumps the panel's own database.
/// </summary>
/// <remarks>
/// <para>
/// The registry is the only place that records which container and which
/// database belong to whom. Every tenant's data survives its loss; the ability
/// to say whose data it is does not — and rebuilding that by hand across a fleet
/// means reading container labels and guessing.
/// </para>
///
/// <para>
/// pgBackRest already covers this database as part of the cluster, physically.
/// That is the right tool for losing a machine. This is the one for losing the
/// registry: a logical dump that restores in minutes into a scratch database,
/// without recovering a whole cluster to read one table.
/// <c>ControlPlane.Deployment.md</c> §9 asked for it; nothing did it.
/// </para>
/// </remarks>
public sealed class RegistrySnapshotJobHandler : IJobHandler
{
    /// <summary>
    /// Not a tenant, and deliberately not a valid slug — a leading underscore
    /// cannot be provisioned, so this can never collide with a customer's name
    /// while still sorting and filtering like the rest of the list.
    /// </summary>
    public const string RegistrySlug = "_registry";

    private readonly ControlPlaneDbContext _db;
    private readonly PgTools _pg;
    private readonly SnapshotSettings _snapshots;
    private readonly TenantDatabaseSettings _database;

    public RegistrySnapshotJobHandler(
        ControlPlaneDbContext db,
        PgTools pg,
        IOptions<SnapshotSettings> snapshots,
        IOptions<TenantDatabaseSettings> database)
    {
        _db = db;
        _pg = pg;
        _snapshots = snapshots.Value;
        _database = database.Value;
    }

    public JobKind Kind => JobKind.RegistrySnapshot;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var database = _database.AdminDatabase;
        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(_database.AdminPassword))
            throw new InvalidOperationException(
                "TenantDatabase:AdminDatabase and AdminPassword must be set — they are how the panel reaches its own registry.");

        Directory.CreateDirectory(_snapshots.Path);

        await context.StepAsync($"Dumping {database}", 20, ct);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{RegistrySlug}-{stamp}.dump";
        var destination = Path.Combine(_snapshots.Path, fileName);

        var connection = _pg.ConnectionString(database, _database.AdminUser, _database.AdminPassword!);
        var (ok, output) = await _pg.DumpAsync(connection, destination, ct);
        if (!string.IsNullOrWhiteSpace(output)) await context.LogAsync(output.Trim(), ct);
        if (!ok)
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            throw new InvalidOperationException($"pg_dump of {database} failed.");
        }

        var size = new FileInfo(destination).Length;
        if (size == 0)
        {
            // A zero-byte dump is the shape of a permissions problem, and it is
            // worse than no dump: it sits in the list looking like a restore point.
            File.Delete(destination);
            throw new InvalidOperationException(
                $"pg_dump of {database} produced an empty file — check that {_database.AdminUser} can read every table in it.");
        }

        _db.Snapshots.Add(new Snapshot
        {
            // No tenant. The list shows it beside the others because that is where
            // someone looks for restore points, but nothing treats it as a tenant.
            TenantId = null,
            TenantSlug = RegistrySlug,
            DatabaseName = database,
            FileName = fileName,
            SizeBytes = size,
            Kind = SnapshotKind.Registry,
            Note = "The fleet registry: which container and database belong to whom.",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        await context.StepAsync($"Registry snapshot taken — {size / 1024} KB", 100, ct);
    }
}

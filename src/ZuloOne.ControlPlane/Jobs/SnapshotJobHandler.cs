using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.Snapshot"/>.</summary>
public sealed record SnapshotPayload(string? Note, SnapshotKind Kind);

/// <summary>
/// Takes a logical dump of one tenant, over the panel's ordinary connection to
/// Postgres.
/// </summary>
public sealed class SnapshotJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly PgTools _pg;
    private readonly SnapshotSettings _settings;
    private readonly Provisioning.TenantDatabaseSettings _database;

    public SnapshotJobHandler(
        ControlPlaneDbContext db, PgTools pg,
        IOptions<SnapshotSettings> settings,
        IOptions<Provisioning.TenantDatabaseSettings> database)
    {
        _db = db;
        _pg = pg;
        _settings = settings.Value;
        _database = database.Value;
    }

    public JobKind Kind => JobKind.Snapshot;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<SnapshotPayload>() ?? new SnapshotPayload(null, SnapshotKind.Manual);
        var tenant = await Load(context, ct);

        await context.StepAsync("Checking there is room", 5, ct);
        Directory.CreateDirectory(_settings.Path);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_settings.Path))!).AvailableFreeSpace;
        if (free < _settings.MinFreeBytes)
        {
            // Refuse UP FRONT rather than discover it at 90%. A dump that fills the
            // disk takes the control plane down with it — and the control plane is
            // what an operator would reach for to fix that.
            throw new InvalidOperationException(
                $"Only {free / 1024 / 1024} MB free where snapshots are kept; {_settings.MinFreeBytes / 1024 / 1024} MB is the floor. Remove old snapshots first.");
        }
        await context.LogAsync($"{free / 1024 / 1024} MB free.", ct);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{tenant.Slug}-{stamp}.dump";
        var destination = Path.Combine(_settings.Path, fileName);

        await context.StepAsync($"Dumping {tenant.DatabaseName}", 20, ct);
        var (ok, output) = await _pg.DumpAsync(Credentials(tenant), destination, ct);
        if (!string.IsNullOrWhiteSpace(output)) await context.LogAsync(output.Trim(), ct);
        if (!ok)
        {
            // Never leave a partial file behind: pg_dump writes as it goes, so a
            // failure leaves something that looks like a snapshot and is not one.
            TryDelete(destination);
            throw new InvalidOperationException("pg_dump failed — see the log above.");
        }

        var size = new FileInfo(destination).Length;
        if (size == 0)
        {
            TryDelete(destination);
            throw new InvalidOperationException(
                "pg_dump produced an EMPTY file. The panel is probably not a member of this tenant's role, so it read nothing and reported success.");
        }

        _db.Snapshots.Add(new Snapshot
        {
            TenantId = tenant.Id,
            TenantSlug = tenant.Slug,
            DatabaseName = tenant.DatabaseName!,
            FileName = fileName,
            SizeBytes = size,
            Kind = payload.Kind,
            Note = payload.Note,
            ImageTag = tenant.ImageTag,
        });
        await _db.SaveChangesAsync(ct);

        await context.StepAsync($"Snapshot taken — {size / 1024} KB", 100, ct);
    }

    private string Credentials(Tenant tenant)
        // As the TENANT's own role when its password is known: that role owns every
        // object, so the dump cannot be silently short. Falling back to the admin
        // role works only because the grants are now explicit.
        => !string.IsNullOrWhiteSpace(tenant.DatabasePassword) && !string.IsNullOrWhiteSpace(tenant.DatabaseRole)
            ? _pg.ConnectionString(tenant.DatabaseName!, tenant.DatabaseRole!, tenant.DatabasePassword!)
            : _pg.ConnectionString(tenant.DatabaseName!, _database.AdminUser, _database.AdminPassword ?? string.Empty);

    private async Task<Tenant> Load(JobContext context, CancellationToken ct)
    {
        var id = context.Job.TenantId ?? throw new InvalidOperationException("A Snapshot job must name a tenant.");
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
            throw new InvalidOperationException($"Tenant '{tenant.Slug}' has no database to snapshot.");
        return tenant;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

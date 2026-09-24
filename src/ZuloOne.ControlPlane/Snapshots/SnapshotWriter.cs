using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Snapshots;

/// <summary>Writes and registers one tenant dump.</summary>
public sealed class SnapshotWriter
{
    private readonly ControlPlaneDbContext _db;
    private readonly PgTools _pg;
    private readonly SnapshotSettings _settings;
    private readonly TenantDatabaseSettings _database;

    public SnapshotWriter(
        ControlPlaneDbContext db,
        PgTools pg,
        IOptions<SnapshotSettings> settings,
        IOptions<TenantDatabaseSettings> database)
    {
        _db = db;
        _pg = pg;
        _settings = settings.Value;
        _database = database.Value;
    }

    public async Task<Snapshot> WriteAsync(
        JobContext context,
        Tenant tenant,
        SnapshotKind kind,
        string? note,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
            throw new InvalidOperationException($"Tenant '{tenant.Slug}' has no database to snapshot.");

        await context.StepAsync("Checking there is room", 5, ct);
        Directory.CreateDirectory(_settings.Path);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_settings.Path))!).AvailableFreeSpace;
        if (free < _settings.MinFreeBytes)
            throw new InvalidOperationException(
                $"Only {free / 1024 / 1024} MB free where snapshots are kept; " +
                $"{_settings.MinFreeBytes / 1024 / 1024} MB is the floor. Remove old snapshots first.");

        await context.LogAsync($"{free / 1024 / 1024} MB free.", ct);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{tenant.Slug}-{stamp}.dump";
        var destination = Path.Combine(_settings.Path, fileName);

        await context.StepAsync($"Dumping {tenant.DatabaseName}", 20, ct);
        var (ok, output) = await _pg.DumpAsync(Credentials(tenant), destination, ct);
        if (!string.IsNullOrWhiteSpace(output)) await context.LogAsync(output.Trim(), ct);
        if (!ok)
        {
            TryDelete(destination);
            throw new InvalidOperationException("pg_dump failed — see the log above.");
        }

        var size = new FileInfo(destination).Length;
        if (size == 0)
        {
            TryDelete(destination);
            throw new InvalidOperationException(
                "pg_dump produced an EMPTY file. The panel is probably not a member of this tenant's role.");
        }

        var snapshot = new Snapshot
        {
            TenantId = tenant.Id,
            TenantSlug = tenant.Slug,
            DatabaseName = tenant.DatabaseName,
            FileName = fileName,
            SizeBytes = size,
            Kind = kind,
            Note = note,
            ImageTag = tenant.ImageTag,
        };
        _db.Snapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);
        await context.StepAsync($"Snapshot taken — {size / 1024} KB", 100, ct);
        return snapshot;
    }

    private string Credentials(Tenant tenant) =>
        !string.IsNullOrWhiteSpace(tenant.DatabasePassword) && !string.IsNullOrWhiteSpace(tenant.DatabaseRole)
            ? _pg.ConnectionString(tenant.DatabaseName!, tenant.DatabaseRole!, tenant.DatabasePassword!)
            : _pg.ConnectionString(tenant.DatabaseName!, _database.AdminUser, _database.AdminPassword ?? string.Empty);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

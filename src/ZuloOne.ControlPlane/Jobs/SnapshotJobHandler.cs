using Microsoft.EntityFrameworkCore;
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
    private readonly SnapshotWriter _writer;

    public SnapshotJobHandler(
        ControlPlaneDbContext db, SnapshotWriter writer)
    {
        _db = db;
        _writer = writer;
    }

    public JobKind Kind => JobKind.Snapshot;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<SnapshotPayload>() ?? new SnapshotPayload(null, SnapshotKind.Manual);
        var tenant = await Load(context, ct);

        await _writer.WriteAsync(context, tenant, payload.Kind, payload.Note, ct);
    }

    private async Task<Tenant> Load(JobContext context, CancellationToken ct)
    {
        var id = context.Job.TenantId ?? throw new InvalidOperationException("A Snapshot job must name a tenant.");
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
            throw new InvalidOperationException($"Tenant '{tenant.Slug}' has no database to snapshot.");
        return tenant;
    }
}

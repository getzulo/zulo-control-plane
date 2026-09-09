using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Snapshots;

using ZuloOne.ControlPlane.Auth;

namespace ZuloOne.ControlPlane.Api;

public record TakeSnapshotRequest(string? Note);

/// <summary>Which restored copy replaces the live tenant's data.</summary>
public record SwapRequest(Guid ScratchTenantId, string ConfirmSlug);

/// <summary>
/// Snapshots, restores and the swap that finishes a restore.
///
/// <para>
/// The shape is deliberately three steps rather than one button. A restore writes
/// into a throwaway tenant with a random name; an operator opens it and confirms
/// the data is what they expected; only then does a swap move it under the live
/// tenant. The alternative — restoring straight over production — is a single
/// irreversible action taken on faith that a backup contains what somebody hopes
/// it contains.
/// </para>
/// </summary>
[ApiController]
[Route("api")]
[Produces("application/json")]
public class SnapshotsController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly IJobQueue _queue;
    private readonly SnapshotSettings _settings;
    private readonly Provisioning.TenantDatabaseProvisioner _databases;

    public SnapshotsController(
        ControlPlaneDbContext db, IJobQueue queue,
        IOptions<SnapshotSettings> settings,
        Provisioning.TenantDatabaseProvisioner databases)
    {
        _db = db;
        _queue = queue;
        _settings = settings.Value;
        _databases = databases;
    }

    private string? Operator => OperatorIdentity.Of(User);

    /// <summary>Every snapshot, or one tenant's, newest first — plus the disk they live on.</summary>
    [HttpGet("snapshots")]
    public async Task<IActionResult> List([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var query = _db.Snapshots.AsNoTracking();
        if (tenantId is not null) query = query.Where(s => s.TenantId == tenantId);

        var snapshots = await query.OrderByDescending(s => s.CreatedAt).Take(200).ToListAsync(ct);

        // Summed in the DATABASE over every row, not over the 200 returned above.
        // The roll-up used to be computed from that truncated list, so the figure
        // an operator reads before deciding whether to prune would understate the
        // moment the fleet passed 200 snapshots — precisely when it starts to
        // matter. The count is returned too, so a truncated list says so.
        var totalCount = await query.CountAsync(ct);
        var totalBytes = await query.SumAsync(s => (long?)s.SizeBytes, ct) ?? 0;

        // Headroom, reported ALONGSIDE the list rather than discovered when a
        // snapshot refuses to start. The floor is enforced server-side; showing the
        // number is what lets someone clear space before they need it.
        long free = 0, total = 0;
        try
        {
            Directory.CreateDirectory(_settings.Path);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_settings.Path))!);
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch { /* a path we cannot stat is not a reason to hide the snapshots */ }

        var rows = snapshots.Select(s => new
        {
            s.Id,
            s.TenantId,
            s.TenantSlug,
            s.DatabaseName,
            s.SizeBytes,
            kind = s.Kind.ToString(),
            s.Note,
            s.ImageTag,
            s.CreatedAt,
            // Whether the file is still where the row says it is. A row without its
            // dump is worse than no row: it reads as a restore point that is not one.
            onDisk = System.IO.File.Exists(Path.Combine(_settings.Path, s.FileName)),
        }).ToList();

        return Ok(new
        {
            snapshots = rows,
            // Says so when the list is only part of the picture, rather than
            // letting the screen imply these are all of them.
            totalCount,
            truncated = totalCount > rows.Count,
            disk = new
            {
                freeBytes = free,
                totalBytes = total,
                usedBySnapshots = totalBytes,
                minFreeBytes = _settings.MinFreeBytes,
                // Below this the server refuses to start a snapshot at all, rather
                // than filling the disk the control plane itself runs on.
                belowFloor = free > 0 && free < _settings.MinFreeBytes,
            },
        });
    }

    /// <summary>
    /// Applies the retention policy now instead of waiting for the schedule.
    /// </summary>
    /// <remarks>
    /// Queued like everything else rather than run inline: the same
    /// single-file execution that keeps the scheduled sweep away from a dump in
    /// progress has to hold for a hurried one, and an operator clicking this
    /// because the disk is filling is exactly when a restore might be running.
    /// </remarks>
    [HttpPost("snapshots/prune")]
    public async Task<IActionResult> Prune(CancellationToken ct)
    {
        var already = await _db.Jobs.AnyAsync(
            j => j.Kind == JobKind.Prune && (j.State == JobState.Queued || j.State == JobState.Running), ct);
        if (already) return Conflict(new { error = "A prune is already queued or running." });

        var job = await _queue.EnqueueAsync(
            JobKind.Prune, tenantId: null, tenantSlug: null, createdBy: OperatorIdentity.Of(User), ct: ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary>Takes a dump of this tenant now. Returns the job that is doing it.</summary>
    [HttpPost("tenants/{id:guid}/snapshot")]
    public async Task<IActionResult> Take(Guid id, [FromBody] TakeSnapshotRequest? request, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
            return BadRequest(new { error = $"'{tenant.Slug}' has no database yet." });

        var job = await _queue.EnqueueAsync(
            JobKind.Snapshot, tenant.Id, tenant.Slug,
            new SnapshotPayload(request?.Note, SnapshotKind.Manual), Operator, ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary>
    /// Rebuilds a snapshot into a throwaway tenant with a random name, so it can be
    /// looked at. Touches nothing that is live.
    /// </summary>
    [HttpPost("snapshots/{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var snapshot = await _db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found", id });
        if (!System.IO.File.Exists(Path.Combine(_settings.Path, snapshot.FileName)))
            return Conflict(new { error = $"The dump file {snapshot.FileName} is no longer on disk." });

        var job = await _queue.EnqueueAsync(
            JobKind.Restore, null, snapshot.TenantSlug, new RestorePayload(snapshot.Id), Operator, ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary>
    /// Puts a verified copy's database under the live tenant.
    ///
    /// The live tenant's slug is retyped: this replaces real data, and the copy's
    /// own random name is not something anyone would type by accident, so confirming
    /// on THAT would confirm nothing.
    /// </summary>
    [HttpPost("tenants/{id:guid}/swap")]
    public async Task<IActionResult> Swap(Guid id, [FromBody] SwapRequest request, CancellationToken ct)
    {
        var live = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (live is null) return NotFound(new { error = "Tenant not found", id });

        var scratch = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.ScratchTenantId, ct);
        if (scratch is null) return NotFound(new { error = "That restored copy is not in the registry." });
        if (scratch.RestoredFromSlug is null)
            return BadRequest(new { error = $"'{scratch.Slug}' is a real tenant, not a restored copy. Only a copy may be swapped in." });
        if (!string.Equals(request.ConfirmSlug, live.Slug, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Retype '{live.Slug}' to confirm. This replaces its data with the copy's." });

        var job = await _queue.EnqueueAsync(
            JobKind.Swap, live.Id, live.Slug, new SwapPayload(scratch.Id), Operator, ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary>
    /// Drops the database a swap set aside. Synchronous — it is one statement, and
    /// unlike the swap there is nothing to watch.
    /// </summary>
    [HttpDelete("tenants/{id:guid}/previous-database")]
    public async Task<IActionResult> DiscardPrevious(Guid id, [FromQuery] string? confirmSlug, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });
        if (string.IsNullOrWhiteSpace(tenant.PreviousDatabaseName))
            return NotFound(new { error = $"'{tenant.Slug}' has no set-aside database." });
        if (!string.Equals(confirmSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Pass confirmSlug={tenant.Slug}. This is the only way back to the data from before the swap." });

        var dropped = tenant.PreviousDatabaseName!;
        await _databases.DropDatabaseAsync(dropped, ct);
        tenant.PreviousDatabaseName = null;
        tenant.PreviousDatabaseAt = null;
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { success = true, dropped });
    }

    /// <summary>Deletes a snapshot's file and its row.</summary>
    [HttpDelete("snapshots/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var snapshot = await _db.Snapshots.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found", id });

        var path = Path.Combine(_settings.Path, snapshot.FileName);
        // The row goes even when the file is already gone — otherwise a failed
        // delete leaves a row pointing at nothing, which reads as a restore point.
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Could not delete the file: {ex.Message}" }); }

        _db.Snapshots.Remove(snapshot);
        await _db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }
}

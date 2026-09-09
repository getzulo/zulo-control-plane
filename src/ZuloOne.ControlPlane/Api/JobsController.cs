using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

/// <summary>
/// What the panel is doing and what it has done. Read-only: jobs are created by
/// the operations that need them, never directly, so that a job always has real
/// work behind it.
/// </summary>
[ApiController]
[Route("api/jobs")]
[Produces("application/json")]
public class JobsController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;

    public JobsController(ControlPlaneDbContext db) => _db = db;

    /// <summary>
    /// Recent activity, newest first. Optionally narrowed to one tenant, or to the
    /// jobs that have not finished — which is what the header indicator wants.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? tenantId, [FromQuery] bool? active, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var query = _db.Jobs.AsNoTracking();
        if (tenantId is not null) query = query.Where(j => j.TenantId == tenantId);
        if (active == true) query = query.Where(j => j.State == JobState.Queued || j.State == JobState.Running);

        var jobs = await query
            .OrderByDescending(j => j.CreatedAt)
            // Clamped rather than trusted: this is the one endpoint the dashboard
            // polls, and an unbounded limit would let a stray query pull the whole
            // history including every job's log on a timer.
            .Take(Math.Clamp(limit, 1, 200))
            .Select(j => Summary(j))
            .ToListAsync(ct);

        return Ok(jobs);
    }

    /// <summary>One job WITH its log — the detail view behind a progress row.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return NotFound(new { error = "Job not found", id });

        return Ok(new
        {
            job.Id,
            kind = job.Kind.ToString(),
            state = job.State.ToString(),
            job.TenantId,
            job.TenantSlug,
            job.Step,
            job.Progress,
            job.Log,
            job.Error,
            job.CreatedBy,
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
        });
    }

    /// <summary>
    /// The list projection. WITHOUT the log: it can be 64 KB per job, and a list of
    /// fifty on a fifteen-second poll would be megabytes a minute for something the
    /// table does not display.
    /// </summary>
    private static object Summary(Job j) => new
    {
        j.Id,
        kind = j.Kind.ToString(),
        state = j.State.ToString(),
        j.TenantId,
        j.TenantSlug,
        j.Step,
        j.Progress,
        j.Error,
        j.CreatedBy,
        j.CreatedAt,
        j.StartedAt,
        j.FinishedAt,
    };
}

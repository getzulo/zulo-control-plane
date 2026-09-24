using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Provisioning.Demo;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

public sealed record ExtendDemoRequest(int Hours);
public sealed record ConfirmDemoRequest(string ConfirmSlug);
public sealed record ClaimDemoRequest(string Email, string? Company = null);

/// <summary>Operator-only controls for the demo fleet.</summary>
[ApiController]
[Route("api/demo/admin")]
[Produces("application/json")]
public sealed class DemoAdminController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly DemoConfig _config;
    private readonly TenantProvisioner _provisioner;
    private readonly DemoPool _pool;
    private readonly TenantContainerService _containers;
    private readonly IJobQueue _jobs;
    private readonly DemoNudge _nudge;

    public DemoAdminController(
        ControlPlaneDbContext db,
        DemoConfig config,
        TenantProvisioner provisioner,
        DemoPool pool,
        TenantContainerService containers,
        IJobQueue jobs,
        DemoNudge nudge)
    {
        _db = db;
        _config = config;
        _provisioner = provisioner;
        _pool = pool;
        _containers = containers;
        _jobs = jobs;
        _nudge = nudge;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var tenants = await _db.Tenants.AsNoTracking()
            .Where(t => t.Demo != null)
            .OrderBy(t => t.Demo).ThenBy(t => t.CreatedAt)
            .Select(t => new
            {
                t.Id,
                t.Slug,
                demo = t.Demo!.Value.ToString(),
                status = t.Status.ToString(),
                health = t.Health.ToString(),
                t.ExpiresAt,
                t.ClaimedAt,
                t.DemoRequestId,
                t.CreatedAt,
            })
            .ToListAsync(ct);
        var queued = await _db.DemoRequests.CountAsync(r => r.State == DemoRequestState.Queued, ct);
        var jobs = await _db.Jobs.AsNoTracking()
            .Where(j => j.Kind == JobKind.DemoProvision || j.Kind == JobKind.DemoTemplate)
            .OrderByDescending(j => j.CreatedAt)
            .Take(20)
            .Select(j => new { j.Id, kind = j.Kind.ToString(), state = j.State.ToString(), j.Step, j.Error, j.CreatedAt })
            .ToListAsync(ct);

        return Ok(new
        {
            enabled = _config.Enabled,
            templateSnapshotId = _config.TemplateSnapshotId,
            poolTarget = _config.PoolTarget,
            maxConcurrent = _config.MaxConcurrent,
            queued,
            tenants,
            jobs,
        });
    }

    [HttpPost("provision")]
    public async Task<IActionResult> Provision(CancellationToken ct)
    {
        var job = await _jobs.EnqueueAsync(
            JobKind.DemoProvision, null, null, createdBy: OperatorIdentity.Of(User), ct: ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    [HttpPost("claim")]
    public async Task<IActionResult> Claim([FromBody] ClaimDemoRequest request, CancellationToken ct)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(request.Email?.Trim(), out var address))
            return BadRequest(new { error = "A valid e-mail address is required." });

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var row = new DemoRequest
        {
            Id = Guid.NewGuid(),
            Email = address.Address.ToLowerInvariant(),
            Company = string.IsNullOrWhiteSpace(request.Company) ? null : request.Company.Trim(),
            Locale = "operator",
            State = DemoRequestState.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        _db.DemoRequests.Add(row);
        await _db.SaveChangesAsync(ct);

        var tenant = await _pool.TryClaimAsync(row.Id, DateTime.UtcNow, _config.LifetimeHours, ct);
        if (tenant is null)
        {
            row.State = DemoRequestState.Rejected;
            row.Note = "No pooled demo was ready for the manual operator claim.";
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _nudge.Signal();
            return Conflict(new { error = "No pooled demo is ready. Provision one and try again." });
        }

        var password = tenant.AdminPasswordOnce;
        row.State = DemoRequestState.Ready;
        row.TenantId = tenant.Id;
        row.TenantSlug = tenant.Slug;
        row.ReadyAt = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(password))
        {
            row.State = DemoRequestState.Failed;
            row.Note = "The claimed workspace had no deliverable password.";
            await _db.Tenants.Where(t => t.Id == tenant.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.ExpiresAt, DateTime.UtcNow)
                    .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _nudge.Signal();
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "The demo could not be issued. The workspace was sent for cleanup." });
        }

        await _db.Tenants.Where(t => t.Id == tenant.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.AdminPasswordOnce, (string?)null)
                .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _nudge.Signal();
        return Ok(new
        {
            requestId = row.Id,
            tenant.Id,
            tenant.Slug,
            url = $"https://{_containers.HostFor(tenant.Slug)}",
            user = _config.UserName,
            password,
            tenant.ExpiresAt,
        });
    }

    [HttpPost("template")]
    public async Task<IActionResult> RebuildTemplate(CancellationToken ct)
    {
        var already = await _db.Jobs.AnyAsync(j => j.Kind == JobKind.DemoTemplate
            && (j.State == JobState.Queued || j.State == JobState.Running), ct);
        if (already) return Conflict(new { error = "A demo template rebuild is already queued or running." });

        var job = await _jobs.EnqueueAsync(
            JobKind.DemoTemplate, null, _config.GoldenSlug,
            createdBy: OperatorIdentity.Of(User), ct: ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    [HttpPost("drain")]
    public async Task<IActionResult> Drain(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var count = await _db.Tenants.Where(t => t.Demo == TenantDemo.Pooled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.ExpiresAt, now)
                .SetProperty(t => t.UpdatedAt, now), ct);
        _nudge.Signal();
        return Ok(new { success = true, markedForReaping = count });
    }

    [HttpPost("{id:guid}/extend")]
    public async Task<IActionResult> Extend(Guid id, [FromBody] ExtendDemoRequest request, CancellationToken ct)
    {
        if (request.Hours <= 0 || request.Hours > _config.MaxExtendHours)
            return BadRequest(new { error = $"Hours must be between 1 and {_config.MaxExtendHours}." });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant?.Demo != TenantDemo.Claimed)
            return NotFound(new { error = "Claimed demo not found.", id });

        var from = tenant.ExpiresAt > DateTime.UtcNow ? tenant.ExpiresAt.Value : DateTime.UtcNow;
        tenant.ExpiresAt = from.AddHours(request.Hours);
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { success = true, tenant.Id, tenant.Slug, tenant.ExpiresAt });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Kill(Guid id, [FromBody] ConfirmDemoRequest request, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null || tenant.Demo is not (TenantDemo.Pooled or TenantDemo.Claimed))
            return NotFound(new { error = "Disposable demo not found.", id });
        if (!string.Equals(request.ConfirmSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Retype '{tenant.Slug}' to confirm." });

        var slug = tenant.Slug;
        await _provisioner.DeleteAsync(tenant, ct);
        _nudge.Signal();
        return Ok(new { success = true, deleted = slug });
    }
}

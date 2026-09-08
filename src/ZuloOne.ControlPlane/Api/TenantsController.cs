using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

/// <summary>What an operator supplies to create a tenant.</summary>
public record CreateTenantRequest(string Slug, string? DisplayName, string AdminEmail, string? ImageTag, string? Plan);

/// <summary>
/// The fleet API behind the dashboard: provision, inspect and control tenants.
/// Destructive by nature — it creates and drops databases and containers — so it
/// belongs behind operator authentication before this is exposed anywhere real.
/// </summary>
[ApiController]
[Route("api/tenants")]
[Produces("application/json")]
public class TenantsController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantProvisioner _provisioner;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly IProvisioningQueue _queue;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(
        ControlPlaneDbContext db,
        TenantProvisioner provisioner,
        TenantContainerService containers,
        TenantHealthProbe health,
        IProvisioningQueue queue,
        ILogger<TenantsController> logger)
    {
        _db = db;
        _provisioner = provisioner;
        _containers = containers;
        _health = health;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>The fleet. Secrets are never projected.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _db.Tenants.AsNoTracking().OrderBy(t => t.Slug).Select(t => Summary(t)).ToListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        return tenant == null ? NotFound(new { error = "Tenant not found", id }) : Ok(Summary(tenant));
    }

    /// <summary>
    /// Provisions a tenant end to end. Runs inline: first boot takes minutes
    /// (migrations, schema sync, metadata compile), so expect a long request —
    /// the dashboard shows the registry row moving through the states meanwhile.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        if (!TenantProvisioner.IsValidSlug(request.Slug))
            return BadRequest(new { error = "Slug must be a DNS label: lowercase letters, digits and dashes." });
        // Separate answer from the shape check on purpose: "reserved" and
        // "malformed" are different problems, and a caller told only "invalid" will
        // keep trying variations of a name they are never going to get.
        if (ReservedSlugs.IsReserved(request.Slug))
            return BadRequest(new { error = $"Slug '{request.Slug?.Trim().ToLowerInvariant()}' is reserved for the platform. Choose another." });
        if (string.IsNullOrWhiteSpace(request.AdminEmail))
            return BadRequest(new { error = "An administrator e-mail is required — that is who gets the invitation." });

        try
        {
            // Two steps: claim the slug now, build it later. The caller gets an
            // immediate answer about whether the name is allowed, and the minutes
            // of database + container + first-boot work happen on a worker whose
            // lifetime is the service's, not this request's.
            var tenant = await _provisioner.RegisterAsync(
                request.Slug, request.DisplayName, request.AdminEmail, request.ImageTag, request.Plan, ct);
            _queue.Enqueue(tenant.Id);

            return Accepted($"/api/tenants/{tenant.Id}", Summary(tenant));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            // The registry keeps the failed row and its error; surface the same.
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/stop")]
    public Task<IActionResult> Stop(Guid id, CancellationToken ct) => Lifecycle(id, ct, async tenant =>
    {
        await _containers.StopAsync(tenant.ContainerId!, ct);
        tenant.Status = TenantStatus.Suspended;
        tenant.Health = TenantHealth.Down;
    });

    [HttpPost("{id:guid}/start")]
    public Task<IActionResult> Start(Guid id, CancellationToken ct) => Lifecycle(id, ct, async tenant =>
    {
        await _containers.StartAsync(tenant.ContainerId!, ct);
        tenant.Status = TenantStatus.Active;
    });

    [HttpPost("{id:guid}/restart")]
    public Task<IActionResult> Restart(Guid id, CancellationToken ct) => Lifecycle(id, ct, async tenant =>
    {
        await _containers.RestartAsync(tenant.ContainerId!, ct);
        tenant.Status = TenantStatus.Active;
    });

    /// <summary>
    /// The administrator password minted at provisioning — returned ONCE, then
    /// erased from the registry.
    ///
    /// Only ever populated when the invitation could not be sent (mail disabled or
    /// SMTP failed). Without this the workspace is unreachable by anyone: the
    /// password went nowhere, and the tenant's own one-shot setup endpoint has
    /// already been consumed, so it cannot be claimed again either.
    ///
    /// Reading it clears it. A second call returns 404, which is the intended
    /// answer — if the operator lost it, the recovery is a password reset inside
    /// the tenant, not another copy from here.
    /// </summary>
    [HttpPost("{id:guid}/admin-password")]
    public async Task<IActionResult> RevealAdminPassword(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant == null) return NotFound(new { error = "Tenant not found", id });
        if (string.IsNullOrEmpty(tenant.AdminPasswordOnce))
            return NotFound(new { error = "No unread password for this tenant — it was either delivered by e-mail or already read once." });

        var password = tenant.AdminPasswordOnce;
        tenant.AdminPasswordOnce = null;
        await SaveAsync(tenant, ct);

        _logger.LogInformation("One-time administrator password for {Slug} was read and erased", tenant.Slug);
        return Ok(new { user = "admin", password, note = "Shown once. Change it after the first sign-in." });
    }

    /// <summary>Container logs — the first thing to look at when a tenant misbehaves.</summary>
    [HttpGet("{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, [FromQuery] int lines = 200, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant?.ContainerId == null) return NotFound(new { error = "Tenant has no container", id });
        return Ok(new { logs = await _containers.TailLogsAsync(tenant.ContainerId, lines, ct) });
    }

    /// <summary>
    /// Destroys the tenant: container, database and role, then the registry row.
    /// Irreversible — there is no backup step yet (§9 is Phase 2), so the caller
    /// must confirm the slug it means to destroy.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] string? confirmSlug, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant == null) return NotFound(new { error = "Tenant not found", id });
        if (!string.Equals(confirmSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Pass confirmSlug={tenant.Slug} to confirm this deletes the tenant and its data." });

        await _provisioner.DeleteAsync(tenant, ct);
        return Ok(new { success = true });
    }

    /// <summary>Fleet roll-up plus a fresh health probe of every tenant.</summary>
    [HttpGet("/api/fleet/health")]
    public async Task<IActionResult> FleetHealth(CancellationToken ct)
    {
        var tenants = await _db.Tenants.ToListAsync(ct);
        foreach (var tenant in tenants.Where(t => t.Status == TenantStatus.Active))
            await _health.RefreshAsync(tenant, _containers.HostFor(tenant.Slug), ct);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            total = tenants.Count,
            active = tenants.Count(t => t.Status == TenantStatus.Active),
            suspended = tenants.Count(t => t.Status == TenantStatus.Suspended),
            failed = tenants.Count(t => t.Status == TenantStatus.Failed),
            down = tenants.Count(t => t.Status == TenantStatus.Active && t.Health == TenantHealth.Down),
            tenants = tenants.OrderBy(t => t.Slug).Select(t => Summary(t)),
        });
    }

    private async Task<IActionResult> Lifecycle(Guid id, CancellationToken ct, Func<Tenant, Task> action)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant == null) return NotFound(new { error = "Tenant not found", id });
        if (string.IsNullOrWhiteSpace(tenant.ContainerId))
            return BadRequest(new { error = "Tenant has no container — provision it first." });

        try
        {
            await action(tenant);
            tenant.LastError = null;
        }
        catch (Exception ex)
        {
            tenant.LastError = ex.Message;
            _logger.LogError(ex, "Lifecycle action failed for tenant {Slug}", tenant.Slug);
            await SaveAsync(tenant, ct);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }

        await SaveAsync(tenant, ct);
        return Ok(Summary(tenant));
    }

    private async Task SaveAsync(Tenant tenant, CancellationToken ct)
    {
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Projection WITHOUT the database password or signing key.</summary>
    private static object Summary(Tenant t) => new
    {
        t.Id,
        t.Slug,
        t.DisplayName,
        status = t.Status.ToString(),
        health = t.Health.ToString(),
        t.ImageTag,
        t.AdminEmail,
        t.Plan,
        t.DatabaseName,
        containerId = t.ContainerId == null ? null : t.ContainerId[..Math.Min(12, t.ContainerId.Length)],
        // The FLAG, never the value — the value comes only from the explicit
        // one-shot endpoint, so it cannot be picked up incidentally by anything
        // that happens to list the fleet.
        hasUnreadAdminPassword = !string.IsNullOrEmpty(t.AdminPasswordOnce),
        t.LastHealthAt,
        t.LastError,
        t.CreatedAt,
        t.UpdatedAt,
    };
}

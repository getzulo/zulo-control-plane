using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

/// <summary>What an operator supplies to create a tenant.</summary>
public record CreateTenantRequest(string Slug, string? DisplayName, string AdminEmail, string? ImageTag, string? Plan);

/// <summary>
/// Which account to reset, and the slug retyped to confirm. A reset is not
/// destructive to data, but it locks out whoever is using that account now.
/// </summary>
public record ResetPasswordRequest(string? UserName, string ConfirmSlug);

/// <summary>
/// What an operator supplies to adopt a hand-deployed tenant. Everything but the
/// slug is inferred: the database and role from the naming convention, the image
/// and container id from what is actually running.
/// </summary>
public record AdoptTenantRequest(
    string Slug, string? DisplayName, string? AdminEmail,
    string? DatabaseName, string? DatabaseRole, string? ImageTag, string? ContainerName);

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
    private readonly IJobQueue _queue;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(
        ControlPlaneDbContext db,
        TenantProvisioner provisioner,
        TenantContainerService containers,
        TenantHealthProbe health,
        IJobQueue queue,
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

            var job = await _queue.EnqueueAsync(
                JobKind.Provision, tenant.Id, tenant.Slug,
                createdBy: User.Identity?.Name ?? User.FindFirst("email")?.Value, ct: ct);

            // The job id rides along so the caller can follow the build without
            // polling the tenant row and guessing which attempt it is watching.
            return Accepted($"/api/tenants/{tenant.Id}", new { tenant = Summary(tenant), jobId = job.Id });
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

    /// <summary>
    /// Brings a tenant that was deployed by hand under management.
    ///
    /// <para>
    /// It already has a database, a role and a running container; what it lacks is a
    /// registry row, which is why it is invisible in the fleet list and excluded
    /// from snapshots, restores and upgrades. Adoption creates the row, takes over
    /// the database credentials — the old password was never recorded, so there is
    /// nothing to keep — and recreates the container from that row.
    /// </para>
    ///
    /// <para>
    /// Signed-in users of that tenant are logged out: the container comes back with
    /// a signing key the registry holds, and the previous one exists nowhere.
    /// </para>
    /// </summary>
    [HttpPost("adopt")]
    public async Task<IActionResult> Adopt([FromBody] AdoptTenantRequest request, CancellationToken ct)
    {
        if (!TenantProvisioner.IsValidSlug(request.Slug))
            return BadRequest(new { error = "Slug must be a DNS label: lowercase letters, digits and dashes." });

        var slug = request.Slug.Trim().ToLowerInvariant();
        if (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct))
            return Conflict(new { error = $"'{slug}' is already in the registry." });

        var database = string.IsNullOrWhiteSpace(request.DatabaseName) ? $"tenant_{slug}" : request.DatabaseName!.Trim();
        var role = string.IsNullOrWhiteSpace(request.DatabaseRole) ? $"tenant_{slug}" : request.DatabaseRole!.Trim();

        var found = await _containers.FindByNameAsync($"zuloone-tenant-{slug}", ct)
                 ?? await _containers.FindByNameAsync(request.ContainerName ?? string.Empty, ct);
        if (found is null)
            return NotFound(new { error = $"No container found for '{slug}'. Pass containerName if it is named differently." });
        var container = found.Value;

        var tenant = new Tenant
        {
            Slug = slug,
            DisplayName = request.DisplayName,
            AdminEmail = request.AdminEmail,
            DatabaseName = database,
            DatabaseRole = role,
            // Taken from what is actually RUNNING, not from the fleet default: an
            // adoption must not silently move the tenant to another version.
            ImageTag = string.IsNullOrWhiteSpace(request.ImageTag) ? container.Image : request.ImageTag!,
            ContainerId = container.Id,
            Status = TenantStatus.Provisioning,
            JwtSigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        var job = await _queue.EnqueueAsync(
            JobKind.Adopt, tenant.Id, tenant.Slug,
            createdBy: User.Identity?.Name ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value, ct: ct);
        return Accepted($"/api/tenants/{tenant.Id}", new { tenant = Summary(tenant), jobId = job.Id });
    }

    /// <summary>
    /// What this tenant is using right now — container and database.
    ///
    /// Read on demand rather than scraped. A fleet this size does not need a
    /// time-series database to answer "what is it doing", and asking directly
    /// cannot drift from reality the way a collector can. It costs about a second,
    /// because a real CPU percentage needs two samples from the Docker stats
    /// stream — a one-shot read reports a meaningless number.
    /// </summary>
    [HttpGet("{id:guid}/stats")]
    public async Task<IActionResult> Stats(Guid id, [FromServices] TenantStatsService stats, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });
        return Ok(await stats.ReadAsync(tenant, ct));
    }

    /// <summary>Who this tenant has, so an operator can pick before resetting.</summary>
    [HttpGet("{id:guid}/users")]
    public async Task<IActionResult> Users(Guid id, [FromServices] TenantAdminService admin, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });
        try
        {
            var users = await admin.ListUsersAsync(tenant, ct);
            return Ok(users.Select(u => new { u.Name, u.Email, u.Active, u.Locked }));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Could not read the tenant's users: {ex.Message}" });
        }
    }

    /// <summary>
    /// Sets a new password for one of the tenant's users and shows it ONCE.
    ///
    /// <para>
    /// Written straight into the tenant's database rather than through its API,
    /// because the situation this exists for is "nobody can sign in", and an
    /// endpoint that requires signing in cannot help with that. The panel already
    /// holds these credentials and can drop the entire database, so this is a
    /// narrower use of authority it has, not new authority.
    /// </para>
    ///
    /// <para>
    /// The slug is retyped. Resetting a password is not destructive to data, but it
    /// locks out whoever is currently using that account, and doing it to the wrong
    /// tenant is an incident.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(
        Guid id, [FromBody] ResetPasswordRequest request,
        [FromServices] TenantAdminService admin, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });
        if (!string.Equals(request?.ConfirmSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Retype '{tenant.Slug}' to confirm. Whoever is using that account now will be locked out." });

        var (found, password, user) = await admin.ResetPasswordAsync(tenant, request?.UserName, ct);
        if (!found)
            return NotFound(new { error = $"No such user in '{tenant.Slug}'." });

        _logger.LogWarning("Operator {Operator} reset the password of {User} on {Slug}",
            User.Identity?.Name ?? "unknown", user, tenant.Slug);

        return Ok(new
        {
            user,
            password,
            url = $"https://{_containers.HostFor(tenant.Slug)}",
            note = "Shown once and stored nowhere. The account is unlocked and must change this at next sign-in.",
        });
    }

    /// <summary>
    /// What the tenant's container actually REPORTS, as opposed to the tag pinned
    /// in the registry. The two disagree when a container was replaced outside the
    /// panel, and that gap is worth seeing rather than assuming away.
    /// </summary>
    [HttpGet("{id:guid}/running")]
    public async Task<IActionResult> Running(
        Guid id, [FromServices] IHttpClientFactory http, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });

        try
        {
            using var client = http.CreateClient("tenant");
            var body = await client.GetStringAsync($"https://{_containers.HostFor(tenant.Slug)}/health", ct);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            return Ok(new
            {
                reachable = true,
                version,
                build = root.TryGetProperty("build", out var b) ? b.GetString() : null,
                startedUtc = root.TryGetProperty("startedUtc", out var s) ? s.GetString() : null,
                pinnedImage = tenant.ImageTag,
                // The pinned tag ends in the version the image was built with, so a
                // mismatch means the running container is not what the registry says.
                matchesPinned = version is not null && tenant.ImageTag.EndsWith($":{version}", StringComparison.Ordinal),
            });
        }
        catch (Exception ex)
        {
            return Ok(new { reachable = false, error = ex.Message, pinnedImage = tenant.ImageTag });
        }
    }

    [HttpPost("{id:guid}/stop")]    public Task<IActionResult> Stop(Guid id, CancellationToken ct) => Lifecycle(id, ct, async tenant =>
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
    /// Stops managing a tenant WITHOUT touching it: the registry row goes, the
    /// container keeps running and the database is left alone.
    ///
    /// <para>
    /// This exists because deletion and disowning are different intentions that
    /// looked identical. An adoption that fails partway leaves a registry row
    /// describing a tenant the panel does not own — and the only way to clear it
    /// was DELETE, which drops the database the row points at. That is how a
    /// hand-deployed tenant's database was destroyed while undoing a failed
    /// REGISTRATION; recovering it took a point-in-time restore.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, [FromQuery] string? confirmSlug, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant == null) return NotFound(new { error = "Tenant not found", id });
        if (!string.Equals(confirmSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Pass confirmSlug={tenant.Slug} to confirm. Its data and container are NOT touched." });

        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Released {Slug} — the registry row is gone; its container and database were left running", tenant.Slug);
        return Ok(new
        {
            success = true,
            released = tenant.Slug,
            note = "The container and database were left as they are. The panel no longer manages this tenant.",
        });
    }

    /// <summary>
    /// Destroys the tenant: container, database and role, then the registry row.
    /// Irreversible — the caller must confirm the slug it means to destroy.
    ///
    /// To stop managing a tenant WITHOUT destroying it, use <see cref="Release"/>.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] string? confirmSlug, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant == null) return NotFound(new { error = "Tenant not found", id });
        if (!string.Equals(confirmSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Pass confirmSlug={tenant.Slug} to confirm this deletes the tenant and its data." });

        // A row whose adoption never completed describes a tenant the panel does not
        // own — its database and container predate the registry entirely. Deleting
        // through this path would destroy someone else's data to clean up our own
        // failed bookkeeping, so it is refused and pointed at /release.
        if (tenant.Status == TenantStatus.Provisioning && tenant.RestoredFromSlug is null && tenant.DatabasePassword is null)
            return Conflict(new
            {
                error = $"'{tenant.Slug}' has a registry row but no credentials of ours — its adoption did not complete, " +
                        $"so its database and container are not this panel's to destroy. Use POST /api/tenants/{id}/release to drop the row and leave it running.",
            });

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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Portal;

public record PortalResetUserRequest(string? UserName);
public record PortalInviteRequest(string Email, string? Role);

/// <summary>
/// What a customer can do to their own stands.
/// </summary>
/// <remarks>
/// <para>
/// A separate controller from <c>TenantsController</c> rather than a policy on it,
/// and the reason is worth stating: the operator's controller is allowed to delete
/// a database, release a tenant, reassign an image and read another customer's
/// row. Adding "unless the caller is a customer" to seventeen endpoints means the
/// eighteenth — written next month by someone who has not read this — is public by
/// default. Here the default is the other way round: there is no way to reach a
/// tenant except <see cref="ResolveAsync"/>, and that joins through membership.
/// </para>
/// <para>
/// Guarded by <see cref="AuthSetup.PortalPolicy"/>, which names the portal scheme
/// and only it. An operator session does NOT work here either, and that symmetry
/// is deliberate — see <see cref="PortalSessionHandler"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/portal/tenants")]
[Produces("application/json")]
[Authorize(Policy = AuthSetup.PortalPolicy)]
public class PortalController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly IJobQueue _queue;
    private readonly FleetConfig _fleet;
    private readonly TenantContainerService _containers;
    private readonly PortalSettings _settings;
    private readonly ILogger<PortalController> _logger;

    public PortalController(
        ControlPlaneDbContext db,
        TenantContainerService containers,
        IOptions<PortalSettings> settings,
        IJobQueue queue,
        FleetConfig fleet,
        ILogger<PortalController> logger)
    {
        _db = db;
        _containers = containers;
        _settings = settings.Value;
        _queue = queue;
        _fleet = fleet;
        _logger = logger;
    }

    /// <summary>
    /// The newest promoted release for the fleet's repository, or null.
    /// </summary>
    /// <remarks>
    /// Releases only — never a CI build. A customer offered `sha-1a2b3c` as
    /// "the latest version" would be offered something nobody promoted, and
    /// `ReleaseVersion` is the one place that decides what a release is.
    /// </remarks>
    private async Task<string?> LatestReleaseAsync(CancellationToken ct)
    {
        var (_, repository) = ReleaseVersion.SplitImage(_fleet.DefaultImage);
        var rows = await _db.Releases.AsNoTracking()
            .Where(r => r.Repository == repository)
            .Select(r => r.Version)
            .ToListAsync(ct);
        return rows
            .Where(ReleaseVersion.IsRelease)
            .OrderByDescending(v => v, ReleaseVersion.CalVer)
            .FirstOrDefault();
    }

    /// <summary>Every stand this account may see.</summary>
    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        if (!_settings.Enabled) return NotFound();

        var accountId = AccountId();
        if (accountId is null) return Unauthorized();

        var rows = await _db.TenantMemberships
            .AsNoTracking()
            .Where(m => m.CustomerAccountId == accountId)
            .Include(m => m.Tenant)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var latest = await LatestReleaseAsync(ct);
        return Ok(new
        {
            tenants = rows
                .Where(m => m.Tenant is not null)
                .OrderBy(m => m.Tenant!.Slug)
                .Select(m => Card(m.Tenant!, m.Role, now, latest))
                .ToList(),
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> One(Guid id, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct);
        if (found.Failure is { } failure) return failure;
        var (tenant, role) = (found.Tenant!, found.Role);

        var running = await _containers.TryGetRunningImageAsync(tenant.ContainerId, ct);
        var card = Card(tenant, role, DateTime.UtcNow);

        return Ok(new
        {
            card.id, card.slug, card.displayName, card.status, card.health, card.url,
            card.plan, card.role, card.demo, card.expiresAt, card.can,
            createdAt = tenant.CreatedAt,
            containerRunning = running is not null,
            // Deliberately NOT tenant.LastError. That field carries operator-facing
            // detail — connection strings in a failed provision, a Docker daemon
            // message naming a host — and this endpoint answers to a customer.
            lastCheckedAt = tenant.LastHealthAt,
        });
    }

    [HttpGet("{id:guid}/stats")]
    public async Task<IActionResult> Stats(Guid id, [FromServices] TenantStatsService stats, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct, PortalCapability.ViewStats);
        if (found.Failure is { } failure) return failure;

        return Ok(await stats.ReadAsync(found.Tenant!, ct));
    }

    /// <summary>
    /// Move the stand to the newest release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only reachable where the plan includes the development module; everywhere
    /// else <see cref="PlanCatalog.Decide"/> refuses with the sentence that says
    /// so, because those stands are carried by the fleet rollout and a button
    /// would promise a choice nobody has.
    /// </para>
    /// <para>
    /// The work itself is the operator's existing <see cref="JobKind.Upgrade"/>
    /// — snapshot, recreate, health gate, roll back on failure. The customer
    /// does not get a lighter path than staff: an upgrade that skipped the
    /// snapshot would be a one-way door on somebody's production data.
    /// </para>
    /// <para>
    /// The target is chosen HERE, not sent by the caller. Accepting a tag from
    /// the browser would let anyone with a session move a stand onto any image
    /// in the registry, including an old one — a downgrade past a forward-only
    /// migration, which is unrecoverable.
    /// </para>
    /// </remarks>
    [HttpPost("{id:guid}/upgrade")]
    public async Task<IActionResult> Upgrade(Guid id, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct, PortalCapability.UpgradeTenant);
        if (found.Failure is { } failure) return failure;

        var tenant = found.Tenant!;
        var latest = await LatestReleaseAsync(ct);

        if (latest is null)
            return BadRequest(new { error = "There is no published release to move to." });
        if (!ReleaseVersion.IsNewer(latest, tenant.ImageTag))
            return BadRequest(new { error = $"This stand is already on {tenant.ImageTag}." });
        if (string.IsNullOrWhiteSpace(tenant.DatabasePassword))
            // No password means no snapshot, and no snapshot means no way back.
            // The operator's endpoint refuses on the same ground.
            return Conflict(new { error = "This stand cannot be snapshotted, so it cannot be upgraded from here. Contact support." });

        var job = await _queue.EnqueueAsync(
            JobKind.Upgrade, tenant.Id, tenant.Slug, new UpgradePayload(latest),
            $"portal:{Email()}", ct);

        _logger.LogInformation(
            "Portal account {Actor} moved tenant {Slug} from {From} to {To}",
            Email(), tenant.Slug, tenant.ImageTag, latest);

        return Accepted(new { jobId = job.Id, version = latest });
    }

    /// <summary>Who has an account inside the stand, so one can be picked for a reset.</summary>
    [HttpGet("{id:guid}/users")]
    public async Task<IActionResult> Users(Guid id, [FromServices] TenantAdminService admin, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct);
        if (found.Failure is { } failure) return failure;

        var users = await admin.ListUsersAsync(found.Tenant!, ct);
        return Ok(new
        {
            users = users.Select(u => new { name = u.Name, email = u.Email, active = u.Active, locked = u.Locked }),
        });
    }

    /// <summary>
    /// Sets a new password for one of the stand's own users and shows it once.
    /// The everyday reason this portal exists.
    /// </summary>
    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(
        Guid id, [FromBody] PortalResetUserRequest request,
        [FromServices] TenantAdminService admin, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct, PortalCapability.ResetUserPassword);
        if (found.Failure is { } failure) return failure;
        var tenant = found.Tenant!;

        var (ok, password, user) = await admin.ResetPasswordAsync(tenant, request?.UserName, ct);
        if (!ok) return NotFound(new { error = "No such user in this stand." });

        // WARNING level and with both identities: this is a customer taking over
        // another person's sign-in, and it is the entry somebody will look for
        // when asked how an account changed hands.
        _logger.LogWarning(
            "Portal account {Actor} reset the password of user {User} in tenant {Slug}",
            Email(), user, tenant.Slug);

        return Ok(new
        {
            user,
            password,
            note = "Shown once. They will be asked to change it when they sign in.",
        });
    }

    [HttpPost("{id:guid}/restart")]
    public Task<IActionResult> Restart(Guid id, CancellationToken ct) =>
        LifecycleAsync(id, PortalCapability.RestartTenant, ct, async tenant =>
        {
            await _containers.RestartAsync(tenant.ContainerId!, ct);
            tenant.Status = TenantStatus.Active;
        });

    [HttpPost("{id:guid}/stop")]
    public Task<IActionResult> Stop(Guid id, CancellationToken ct) =>
        LifecycleAsync(id, PortalCapability.StopStartTenant, ct, async tenant =>
        {
            await _containers.StopAsync(tenant.ContainerId!, ct);
            tenant.Status = TenantStatus.Suspended;
            tenant.Health = TenantHealth.Down;
        });

    /// <summary>
    /// Starts a stand the customer stopped themselves.
    /// </summary>
    /// <remarks>
    /// Refuses when the stand was not stopped from here, and that asymmetry is the
    /// whole point. <see cref="TenantStatus.Suspended"/> is also what an operator
    /// sets when a subscription is not settled; without this check, "start" would
    /// be a button that un-suspends an unpaid stand, and the licence gate in
    /// <see cref="PlanCatalog"/> would be one click deep.
    /// </remarks>
    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct);
        if (found.Failure is { } failure) return failure;
        var tenant = found.Tenant!;

        if (found.Role != MembershipRole.Owner)
            return Forbid403("Only the stand's owner can do this.");

        if (tenant.Status != TenantStatus.Suspended)
            return BadRequest(new { error = $"This stand is {tenant.Status.ToString().ToLowerInvariant()}, not stopped." });

        if (!tenant.StoppedByCustomer)
        {
            return Forbid403(
                "This stand was suspended by us, not from here. Settling the subscription is what brings it back.");
        }

        if (string.IsNullOrWhiteSpace(tenant.ContainerId))
            return BadRequest(new { error = "This stand has no container." });

        try
        {
            await _containers.StartAsync(tenant.ContainerId!, ct);
            tenant.Status = TenantStatus.Active;
            tenant.StoppedByCustomer = false;
            tenant.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Portal start failed for tenant {Slug}", tenant.Slug);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "The stand did not start. We have been told about it." });
        }

        _logger.LogInformation("Portal account {Actor} started tenant {Slug}", Email(), tenant.Slug);
        return Ok(Card(tenant, found.Role, DateTime.UtcNow));
    }

    [HttpGet("{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, [FromQuery] int lines, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct, PortalCapability.ViewLogs);
        if (found.Failure is { } failure) return failure;
        var tenant = found.Tenant!;

        if (string.IsNullOrWhiteSpace(tenant.ContainerId))
            return Ok(new { logs = string.Empty });

        // Clamped. The operator's endpoint takes what it is given because an
        // operator debugging an incident may genuinely want everything; here an
        // unbounded number is a way to make the Docker daemon do arbitrary work
        // from the public internet.
        var take = Math.Clamp(lines <= 0 ? 200 : lines, 1, 2000);
        return Ok(new { logs = await _containers.TailLogsAsync(tenant.ContainerId!, take, ct) });
    }

    // -------------------------------------------------------------- members ---

    [HttpGet("{id:guid}/members")]
    public async Task<IActionResult> Members(Guid id, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct);
        if (found.Failure is { } failure) return failure;

        var members = await _db.TenantMemberships
            .AsNoTracking()
            .Where(m => m.TenantId == id)
            .Include(m => m.Account)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return Ok(new
        {
            members = members.Select(m => new
            {
                id = m.Id,
                email = m.Account?.Email,
                displayName = m.Account?.DisplayName,
                role = m.Role.ToString(),
                since = m.CreatedAt,
                // So the UI can render "you" and refuse to offer self-removal.
                isSelf = m.CustomerAccountId == AccountId(),
            }),
        });
    }

    /// <summary>
    /// Lets another person in. They must already have a verified account — this
    /// grants access, it does not create one.
    /// </summary>
    /// <remarks>
    /// Requiring an existing verified account is the simple version on purpose.
    /// An invitation that creates an account would have to mint a credential for
    /// an address nobody here has proved, on the say-so of a customer — which is a
    /// way to have our mail server send sign-up links to strangers.
    /// </remarks>
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] PortalInviteRequest request, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct, PortalCapability.ManageMembers);
        if (found.Failure is { } failure) return failure;

        var email = request?.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email)) return BadRequest(new { error = "Which address?" });

        var role = string.Equals(request!.Role, "owner", StringComparison.OrdinalIgnoreCase)
            ? MembershipRole.Owner
            : MembershipRole.Member;

        var account = await _db.CustomerAccounts
            .FirstOrDefaultAsync(a => a.Email == email && a.EmailVerifiedAt != null, ct);

        if (account is null)
        {
            return NotFound(new
            {
                error = "Nobody with a confirmed account uses that address yet. Ask them to sign up first, then add them.",
            });
        }

        var existing = await _db.TenantMemberships
            .FirstOrDefaultAsync(m => m.TenantId == id && m.CustomerAccountId == account.Id, ct);

        if (existing is not null)
        {
            existing.Role = role;
            await _db.SaveChangesAsync(ct);
            return Ok(new { updated = true, email, role = role.ToString() });
        }

        _db.TenantMemberships.Add(new TenantMembership
        {
            TenantId = id,
            CustomerAccountId = account.Id,
            Role = role,
            GrantedBy = Email(),
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Portal account {Actor} added {Email} to tenant {Slug} as {Role}",
            Email(), email, found.Tenant!.Slug, role);

        return Ok(new { added = true, email, role = role.ToString() });
    }

    [HttpDelete("{id:guid}/members/{membershipId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid membershipId, CancellationToken ct)
    {
        var found = await ResolveAsync(id, ct, PortalCapability.ManageMembers);
        if (found.Failure is { } failure) return failure;

        var membership = await _db.TenantMemberships
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.TenantId == id, ct);
        if (membership is null) return NotFound(new { error = "No such member of this stand." });

        // The last owner cannot be removed — by themselves or by a co-owner. A
        // stand with members but no owner can never be administered again from
        // here, and the recovery is an operator doing it by hand.
        if (membership.Role == MembershipRole.Owner)
        {
            var owners = await _db.TenantMemberships
                .CountAsync(m => m.TenantId == id && m.Role == MembershipRole.Owner, ct);
            if (owners <= 1)
                return BadRequest(new { error = "A stand must keep at least one owner. Make somebody else an owner first." });
        }

        _db.TenantMemberships.Remove(membership);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Portal account {Actor} removed a member from tenant {Slug}", Email(), found.Tenant!.Slug);
        return Ok(new { removed = true });
    }

    // ---------------------------------------------------------------- plumbing ---

    private readonly record struct Resolved(Tenant? Tenant, MembershipRole Role, IActionResult? Failure);

    /// <summary>
    /// The ONLY way this controller obtains a tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every endpoint goes through here, and here the tenant is reached by joining
    /// from the caller's membership rather than by looking up an id and checking
    /// afterwards. The difference matters: a forgotten check is invisible, whereas
    /// a forgotten join does not compile into anything that returns a tenant at
    /// all.
    /// </para>
    /// <para>
    /// A stand the caller is not a member of answers 404, not 403. 403 would
    /// confirm the id exists, which turns this into a way to enumerate the fleet
    /// one guid at a time.
    /// </para>
    /// </remarks>
    private async Task<Resolved> ResolveAsync(
        Guid tenantId, CancellationToken ct, PortalCapability capability = PortalCapability.ViewTenant)
    {
        if (!_settings.Enabled) return new(null, default, NotFound());

        var accountId = AccountId();
        if (accountId is null) return new(null, default, Unauthorized());

        var membership = await _db.TenantMemberships
            .Include(m => m.Tenant)
            .FirstOrDefaultAsync(m => m.CustomerAccountId == accountId && m.TenantId == tenantId, ct);

        if (membership?.Tenant is null)
            return new(null, default, NotFound(new { error = "No such stand." }));

        var decision = PlanCatalog.Decide(capability, membership.Tenant, membership.Role, DateTime.UtcNow);
        if (!decision.Allowed)
            return new(null, default, Forbid403(decision.Reason!));

        return new(membership.Tenant, membership.Role, null);
    }

    private async Task<IActionResult> LifecycleAsync(
        Guid id, PortalCapability capability, CancellationToken ct, Func<Tenant, Task> action)
    {
        var found = await ResolveAsync(id, ct, capability);
        if (found.Failure is { } failure) return failure;
        var tenant = found.Tenant!;

        if (string.IsNullOrWhiteSpace(tenant.ContainerId))
            return BadRequest(new { error = "This stand has no container." });

        try
        {
            await action(tenant);
            // A customer stopping their own stand has to be distinguishable from an
            // operator suspending an unpaid one — otherwise Start becomes a way out
            // of suspension. See Start.
            if (capability == PortalCapability.StopStartTenant && tenant.Status == TenantStatus.Suspended)
                tenant.StoppedByCustomer = true;

            tenant.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The exception text is logged, never returned. Docker's messages name
            // container ids, image tags and host paths — the operator's endpoint
            // returns them because an operator is entitled to them.
            _logger.LogError(ex, "Portal lifecycle action failed for tenant {Slug}", tenant.Slug);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "That did not work. We have been told about it." });
        }

        _logger.LogInformation(
            "Portal account {Actor} ran {Action} on tenant {Slug}", Email(), capability, tenant.Slug);
        return Ok(Card(tenant, found.Role, DateTime.UtcNow));
    }

    /// <summary>
    /// A 403 that carries a readable reason.
    /// </summary>
    /// <remarks>
    /// <c>Forbid()</c> is not usable here: it asks the authentication scheme to
    /// write the challenge, which for a bearer scheme produces an empty body — so
    /// the carefully-worded sentence from <see cref="PlanCatalog"/> would never
    /// reach the screen it was written for.
    /// </remarks>
    private IActionResult Forbid403(string reason) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = reason });

    private Guid? AccountId() =>
        Guid.TryParse(User.FindFirst(PortalSessionHandler.AccountIdClaim)?.Value, out var id) ? id : null;

    private string Email() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "(unknown)";

    /// <summary>
    /// What a customer is shown about a stand — and, as importantly, what they
    /// are not: no database name, no role, no password, no image tag, no container
    /// id, no last error. Those are the operator's <c>Summary</c>, and several of
    /// them are credentials.
    /// </summary>
    private TenantCard Card(Tenant tenant, MembershipRole role, DateTime now, string? latest = null)
    {
        var facts = PlanCatalog.Describe(tenant.Plan);
        return new TenantCard(
            id: tenant.Id,
            slug: tenant.Slug,
            displayName: tenant.DisplayName ?? tenant.Slug,
            status: tenant.Status.ToString(),
            health: tenant.Health.ToString(),
            url: $"https://{_containers.HostFor(tenant.Slug)}",
            plan: facts,
            role: role.ToString(),
            demo: tenant.Demo is not null,
            expiresAt: tenant.ExpiresAt,
            // The version the stand actually runs, and the newest one there is.
            // Both are shown even where the customer cannot act on them: "you
            // are on 2026.9.4, the current release is 2026.9.7" is the single
            // most common support question, and answering it costs nothing.
            version: tenant.ImageTag,
            latestVersion: latest,
            updateAvailable: ReleaseVersion.IsNewer(latest, tenant.ImageTag),
            // Computed here rather than in the UI. A screen that decides for
            // itself which buttons to draw will disagree with the server the first
            // time a rule changes, and the disagreement shows up as a button that
            // 403s.
            can: Enum.GetValues<PortalCapability>()
                .ToDictionary(
                    c => char.ToLowerInvariant(c.ToString()[0]) + c.ToString()[1..],
                    c => PlanCatalog.Decide(c, tenant, role, now).Allowed));
    }

    private sealed record TenantCard(
        Guid id, string slug, string displayName, string status, string health, string url,
        PlanFacts plan, string role, bool demo, DateTime? expiresAt,
        string? version, string? latestVersion, bool updateAvailable,
        Dictionary<string, bool> can);
}

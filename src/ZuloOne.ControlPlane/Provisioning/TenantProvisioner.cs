using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Drives a tenant from nothing to serving (ControlPlane.Deployment.md §6):
/// database → container → wait for ready → seed the administrator → invite them.
///
/// Every step is recorded on the registry row as it happens, so a control-plane
/// restart mid-provision leaves evidence rather than a mystery, and a failure
/// rolls the half-built tenant back instead of leaving an orphan container and a
/// database nobody can attribute.
/// </summary>
public sealed class TenantProvisioner
{
    // A slug becomes a DNS label AND a SQL identifier, so it is restricted to what
    // is safe in both — this is also what makes the quoted identifiers in
    // TenantDatabaseProvisioner safe.
    private static readonly Regex SlugPattern = new("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

    private readonly ControlPlaneDbContext _db;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly TenantInviteService _invites;
    private readonly FleetConfig _fleet;
    private readonly ILogger<TenantProvisioner> _logger;

    public TenantProvisioner(
        ControlPlaneDbContext db,
        TenantDatabaseProvisioner databases,
        TenantContainerService containers,
        TenantHealthProbe health,
        TenantInviteService invites,
        FleetConfig fleet,
        ILogger<TenantProvisioner> logger)
    {
        _db = db;
        _databases = databases;
        _containers = containers;
        _health = health;
        _invites = invites;
        _fleet = fleet;
        _logger = logger;
    }

    public static bool IsValidSlug(string? slug) => !string.IsNullOrWhiteSpace(slug) && SlugPattern.IsMatch(slug);

    /// <summary>
    /// Claims the slug and records the intent — fast, and safe to run on an HTTP
    /// request. Everything slow happens later in <see cref="BuildAsync"/>.
    ///
    /// Split from the build deliberately: the caller needs an immediate answer to
    /// "is this slug even allowed", and validation failures should be a 400 rather
    /// than a row that turns Failed a minute later.
    /// </summary>
    public async Task<Tenant> RegisterAsync(
        string slug, string? displayName, string adminEmail, string? imageTag, string? plan, CancellationToken ct = default)
    {
        slug = slug.Trim().ToLowerInvariant();
        if (!IsValidSlug(slug))
            throw new ArgumentException("Slug must be a DNS label: lowercase letters, digits and dashes.", nameof(slug));
        // Enforced HERE as well as in the API, because this is the only path every
        // caller shares — a future admin tool, a seeding script or a migration that
        // provisions directly would otherwise bypass the check entirely.
        if (ReservedSlugs.IsReserved(slug))
            throw new ArgumentException($"Slug '{slug}' is reserved for the platform.", nameof(slug));
        if (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct))
            throw new InvalidOperationException($"Tenant '{slug}' already exists.");

        var tenant = new Tenant
        {
            Slug = slug,
            DisplayName = displayName,
            AdminEmail = adminEmail,
            Plan = plan,
            ImageTag = string.IsNullOrWhiteSpace(imageTag) ? _fleet.DefaultImage : imageTag!,
            Status = TenantStatus.Provisioning,
            // A key per tenant: a token minted for one is meaningless to any other.
            JwtSigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);
        return tenant;
    }

    /// <summary>
    /// Builds a registered tenant: database → container → wait for ready → seed the
    /// administrator → invite. Minutes, not seconds.
    ///
    /// Runs on a background worker with its own lifetime, NOT on the HTTP request
    /// that asked for the tenant. It used to run inline, which meant a closed tab
    /// or a proxy's idle timeout cancelled provisioning halfway — see the catch
    /// block for what that left behind.
    /// </summary>
    /// <param name="job">
    /// Where the steps are reported, so an operator watching a build that takes
    /// minutes can see which of them it is on. Optional: the provisioner is still
    /// callable without a job, and every call is null-guarded rather than requiring
    /// a stub.
    /// </param>
    public async Task<Tenant> BuildAsync(Tenant tenant, JobContext? job = null, CancellationToken ct = default)
    {
        var slug = tenant.Slug;
        var adminEmail = tenant.AdminEmail ?? string.Empty;

        try
        {
            if (job is not null) await job.StepAsync("Creating the database", 10, ct);
            var (database, role, password, connectionString) = await _databases.CreateAsync(slug, ct);
            tenant.DatabaseName = database;
            tenant.DatabaseRole = role;
            tenant.DatabasePassword = password;
            await SaveAsync(tenant, ct);

            if (job is not null) await job.StepAsync("Starting the container", 25, ct);
            tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, ct);
            await SaveAsync(tenant, ct);

            // First boot runs migrations, schema sync and a metadata compile, so
            // readiness is minutes away, not seconds — seeding before it is ready
            // would just 404.
            if (job is not null)
                await job.StepAsync($"Waiting for first boot (up to {_fleet.ReadinessTimeoutSeconds}s)", 40, ct);
            var ready = await _health.WaitUntilReadyAsync(
                _containers.HostFor(slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
            if (!ready)
                throw new TimeoutException($"Tenant did not report ready within {_fleet.ReadinessTimeoutSeconds}s.");

            // POST /api/auth/setup is one-shot and anonymous: it only works while
            // the user table is empty, which is exactly now.
            if (job is not null) await job.StepAsync("Seeding the administrator", 80, ct);
            var invitePassword = await _invites.SeedAdministratorAsync(_containers.HostFor(slug), adminEmail, ct);
            var invited = await _invites.SendInviteAsync(tenant, _containers.HostFor(slug), invitePassword, ct);

            // Hold the password only when nothing else carries it. If the mail went
            // out, that IS the delivery and a second copy here would be liability
            // for no gain. If it did not, this row is the only thing standing
            // between the customer and a workspace nobody can sign in to.
            tenant.AdminPasswordOnce = invited ? null : invitePassword;
            if (job is not null)
            {
                await job.LogAsync(invited
                    ? $"Invitation sent to {adminEmail}."
                    : "Mail is disabled or the send failed — the one-time password is held for a single read on the tenant's row.", ct);
            }

            tenant.Status = TenantStatus.Active;
            tenant.Health = TenantHealth.Ok;
            tenant.LastHealthAt = DateTime.UtcNow;
            tenant.LastError = null;
            await SaveAsync(tenant, ct);

            if (job is not null) await job.StepAsync($"Active at {_containers.HostFor(slug)}", 100, ct);
            _logger.LogInformation("Tenant {Slug} is active at {Host}", slug, _containers.HostFor(slug));
            return tenant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning tenant {Slug} failed — rolling back", slug);

            // CancellationToken.None from here down, deliberately. The commonest
            // way to reach this block is the caller giving up — a closed tab, a
            // proxy cutting an idle request — and `ct` is then already cancelled.
            // Passing it on made SaveChangesAsync throw on the very next line, so
            // the exception escaped this handler and the rollback below never ran:
            // a live container and a created database were left behind, the row
            // still reading Provisioning, with nothing in the service that ever
            // revisits it. The tenant then sat on its public hostname with an
            // empty user table until a human noticed.
            //
            // Cleanup must not be contingent on whether anyone is still listening.
            tenant.Status = TenantStatus.Failed;
            tenant.LastError = ex.Message;
            await SaveAsync(tenant, CancellationToken.None);

            if (job is not null)
                await job.StepAsync("Failed — rolling back", job.Job.Progress, CancellationToken.None);

            // Roll the half-built tenant back so a retry starts clean. The row is
            // KEPT (failed, with its error) — silently vanishing would hide the
            // failure from whoever asked for the tenant.
            await SafeRollbackAsync(tenant, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Tears a tenant down: container, then database and role.</summary>
    public async Task DeleteAsync(Tenant tenant, CancellationToken ct = default)
    {
        tenant.Status = TenantStatus.Deleting;
        await SaveAsync(tenant, ct);

        if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
            await _containers.RemoveAsync(tenant.ContainerId!, ct);
        // Only HERE, never on a recreate: the ring is what makes the tenant's stored
        // secrets readable, and it must outlive every upgrade.
        await _containers.RemoveVolumeAsync(tenant.Slug, ct);
        await _databases.DropAsync(tenant.DatabaseName, tenant.DatabaseRole, ct);

        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Tenant {Slug} deleted", tenant.Slug);
    }

    private async Task SafeRollbackAsync(Tenant tenant, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
                await _containers.RemoveAsync(tenant.ContainerId!, ct);
            await _databases.DropAsync(tenant.DatabaseName, tenant.DatabaseRole, ct);

            tenant.ContainerId = null;
            tenant.DatabaseName = null;
            tenant.DatabaseRole = null;
            tenant.DatabasePassword = null;
            await SaveAsync(tenant, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback for tenant {Slug} was incomplete — check for orphans", tenant.Slug);
        }
    }

    private async Task SaveAsync(Tenant tenant, CancellationToken ct)
    {
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

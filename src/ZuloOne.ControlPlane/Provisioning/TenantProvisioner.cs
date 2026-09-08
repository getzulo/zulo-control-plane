using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    private readonly FleetSettings _fleet;
    private readonly ILogger<TenantProvisioner> _logger;

    public TenantProvisioner(
        ControlPlaneDbContext db,
        TenantDatabaseProvisioner databases,
        TenantContainerService containers,
        TenantHealthProbe health,
        TenantInviteService invites,
        IOptions<FleetSettings> fleet,
        ILogger<TenantProvisioner> logger)
    {
        _db = db;
        _databases = databases;
        _containers = containers;
        _health = health;
        _invites = invites;
        _fleet = fleet.Value;
        _logger = logger;
    }

    public static bool IsValidSlug(string? slug) => !string.IsNullOrWhiteSpace(slug) && SlugPattern.IsMatch(slug);

    public async Task<Tenant> ProvisionAsync(
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

        try
        {
            var (database, role, password, connectionString) = await _databases.CreateAsync(slug, ct);
            tenant.DatabaseName = database;
            tenant.DatabaseRole = role;
            tenant.DatabasePassword = password;
            await SaveAsync(tenant, ct);

            tenant.ContainerId = await _containers.RunAsync(tenant, connectionString, ct);
            await SaveAsync(tenant, ct);

            // First boot runs migrations, schema sync and a metadata compile, so
            // readiness is minutes away, not seconds — seeding before it is ready
            // would just 404.
            var ready = await _health.WaitUntilReadyAsync(
                _containers.HostFor(slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
            if (!ready)
                throw new TimeoutException($"Tenant did not report ready within {_fleet.ReadinessTimeoutSeconds}s.");

            // POST /api/auth/setup is one-shot and anonymous: it only works while
            // the user table is empty, which is exactly now.
            var invitePassword = await _invites.SeedAdministratorAsync(_containers.HostFor(slug), adminEmail, ct);
            await _invites.SendInviteAsync(tenant, _containers.HostFor(slug), invitePassword, ct);

            tenant.Status = TenantStatus.Active;
            tenant.Health = TenantHealth.Ok;
            tenant.LastHealthAt = DateTime.UtcNow;
            tenant.LastError = null;
            await SaveAsync(tenant, ct);

            _logger.LogInformation("Tenant {Slug} is active at {Host}", slug, _containers.HostFor(slug));
            return tenant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning tenant {Slug} failed — rolling back", slug);
            tenant.Status = TenantStatus.Failed;
            tenant.LastError = ex.Message;
            await SaveAsync(tenant, ct);

            // Roll the half-built tenant back so a retry starts clean. The row is
            // KEPT (failed, with its error) — silently vanishing would hide the
            // failure from whoever asked for the tenant.
            await SafeRollbackAsync(tenant, ct);
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

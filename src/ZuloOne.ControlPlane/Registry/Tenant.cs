using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Registry;

/// <summary>Where a tenant is in its life: the registry's source of truth.</summary>
public enum TenantStatus
{
    /// <summary>Being built — database, container, admin. Not yet usable.</summary>
    Provisioning,

    /// <summary>Running and reachable at its subdomain.</summary>
    Active,

    /// <summary>Deliberately stopped (billing, maintenance). Data intact.</summary>
    Suspended,

    /// <summary>Provisioning gave up; <see cref="Tenant.LastError"/> says why.</summary>
    Failed,

    /// <summary>Being torn down.</summary>
    Deleting,
}

/// <summary>Whether the tenant answered its last health probe.</summary>
public enum TenantHealth
{
    Unknown,
    Ok,
    Down,
}

/// <summary>
/// One customer instance: a container, its own database and a subdomain
/// (docs/architecture/ControlPlane.Deployment.md §2). This row is what lets the
/// fleet be rebuilt after a control-plane restart — it is the only place that
/// remembers which container and database belong to whom.
/// </summary>
public class Tenant
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>DNS label: the tenant answers at <c>{Slug}.{root domain}</c>.</summary>
    [Required]
    [MaxLength(63)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;

    /// <summary>Database created for this tenant on the managed Postgres.</summary>
    [MaxLength(63)]
    public string? DatabaseName { get; set; }

    /// <summary>Least-privilege role that owns <see cref="DatabaseName"/>.</summary>
    [MaxLength(63)]
    public string? DatabaseRole { get; set; }

    /// <summary>
    /// The tenant's database password and JWT signing key. Kept here because the
    /// control plane must be able to recreate the container after a restart, and
    /// there is no secrets manager yet — the registry database is the boundary.
    /// </summary>
    [MaxLength(200)]
    public string? DatabasePassword { get; set; }

    [MaxLength(200)]
    public string? JwtSigningKey { get; set; }

    /// <summary>Pinned image tag, so upgrades are per-tenant and staged (§8).</summary>
    [Required]
    [MaxLength(200)]
    public string ImageTag { get; set; } = "zuloone/core:dev";

    [MaxLength(64)]
    public string? ContainerId { get; set; }

    /// <summary>Who to contact — and who the seeded administrator is.</summary>
    [MaxLength(320)]
    public string? AdminEmail { get; set; }

    [MaxLength(50)]
    public string? Plan { get; set; }

    public TenantHealth Health { get; set; } = TenantHealth.Unknown;

    public DateTime? LastHealthAt { get; set; }

    /// <summary>Why provisioning or a lifecycle action failed; null when fine.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

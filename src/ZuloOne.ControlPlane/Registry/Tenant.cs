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

    /// <summary>
    /// The administrator password minted at provisioning, held ONLY until it has
    /// been read once, then nulled.
    ///
    /// It exists because the alternative is worse. The password is generated during
    /// provisioning and, with mail disabled, delivered nowhere; the tenant then
    /// reaches Active with an `admin` account whose password exists nowhere at all,
    /// and its own `setup-required` already answers false — so it cannot be claimed
    /// again either. The workspace is simply unreachable by anyone.
    ///
    /// Cleared the moment it is read, and never written at all when the invitation
    /// was actually sent: in that case the mail is the delivery, and a second copy
    /// sitting in the registry is pure liability.
    /// </summary>
    [MaxLength(200)]
    public string? AdminPasswordOnce { get; set; }

    [MaxLength(50)]
    public string? Plan { get; set; }

    public TenantHealth Health { get; set; } = TenantHealth.Unknown;

    /// <summary>
    /// Set on a throwaway copy produced by a restore: which tenant it came from,
    /// which snapshot, and when.
    ///
    /// A restored copy is a tenant in every mechanical sense — its own database,
    /// role, container and hostname — and without this it would be indistinguishable
    /// from a real customer in the fleet list. It also decides what the panel offers
    /// for the row: a copy can be swapped in or discarded, a customer cannot.
    /// </summary>
    [MaxLength(63)]
    public string? RestoredFromSlug { get; set; }

    public Guid? RestoredFromSnapshotId { get; set; }

    public DateTime? RestoredAt { get; set; }

    /// <summary>
    /// The database this tenant used BEFORE a swap, kept under a timestamped name.
    ///
    /// This is the undo, and it is why a swap is not a leap of faith: renaming the
    /// old database aside costs nothing and takes no time, where dumping it at that
    /// moment would cost both. It is discarded by an explicit action — until then it
    /// occupies disk, which is the trade being made deliberately.
    /// </summary>
    [MaxLength(63)]
    public string? PreviousDatabaseName { get; set; }

    public DateTime? PreviousDatabaseAt { get; set; }

    public DateTime? LastHealthAt { get; set; }

    /// <summary>Why provisioning or a lifecycle action failed; null when fine.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

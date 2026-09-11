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
/// Who created the tenant's database and container — which is the only honest
/// basis for deciding whether the panel may destroy them.
/// </summary>
public enum TenantOrigin
{
    /// <summary>
    /// The panel built it. Deleting undoes the panel's own work, so it is allowed
    /// with the usual slug confirmation.
    /// </summary>
    Provisioned,

    /// <summary>
    /// The panel took over something that already existed. It never created the
    /// data, so it must not be able to destroy it — <c>POST /release</c> drops the
    /// registry row and leaves the tenant running.
    /// </summary>
    Adopted,
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

    /// <summary>
    /// Where the database and container came from: did the panel create them, or
    /// did it take over ones that already existed?
    /// </summary>
    /// <remarks>
    /// This is what decides whether the panel may destroy them, and status cannot
    /// answer it. Delete used to refuse on
    /// <c>Status == Provisioning &amp;&amp; RestoredFromSlug is null &amp;&amp; DatabasePassword is null</c>,
    /// which holds only if adoption fails on its FIRST step. Fail later — on the
    /// container recreate, or waiting for health — and the row carries a password
    /// with status Failed, so none of the three conditions hold and Delete would
    /// happily drop a database the panel never created.
    ///
    /// <para>
    /// An operator looking at a failed adoption reasonably reads the delete button
    /// as "clear this bad registry entry". For an adopted tenant it must never
    /// mean "and take the customer's data with it" — that is what
    /// <c>POST /release</c> is for.
    /// </para>
    /// </remarks>
    public TenantOrigin Origin { get; set; } = TenantOrigin.Provisioned;

    /// <summary>Why provisioning or a lifecycle action failed; null when fine.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Which models this tenant installs, as JSON: <c>{"Sales":"1.2.0","Common":""}</c>.
    /// An empty version means "whatever the image carries".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NULL means fall through to <c>Fleet:Packages</c></b>, and that is the whole
    /// compatibility story: every tenant that existed before this column keeps the
    /// fleet-wide list it has always had, and only a tenant somebody has deliberately
    /// pinned stops following it.
    /// </para>
    ///
    /// <para>
    /// Read at container creation, not at runtime — <see cref="Provisioning.TenantContainerService"/>
    /// turns it into <c>ZuloOne__Packages__Install</c> and the tenant acts on it at
    /// boot. So changing this column does nothing until the container is recreated,
    /// which means a model rollout costs the same snapshot, health gate and pin-back
    /// as an image upgrade. "Just change the models" is not a cheaper operation; it
    /// is the same one.
    /// </para>
    ///
    /// <para>
    /// Sits on the tenant rather than in <c>Settings</c> for the same reason
    /// <see cref="ImageTag"/> does: it is per-tenant state, and the settings store is a
    /// declared catalogue rendered onto a screen, where per-tenant rows would appear
    /// as unrecognised keys and bloat a singleton cache.
    /// </para>
    /// </remarks>
    [MaxLength(2000)]
    public string? Models { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Snapshots;

/// <summary>Why a snapshot was taken. Decides what may sweep it away.</summary>
public enum SnapshotKind
{
    /// <summary>An operator asked for it. Never removed automatically.</summary>
    Manual,

    /// <summary>Taken immediately before an upgrade — this IS the rollback (§8).</summary>
    PreUpgrade,

    /// <summary>Taken from the live tenant just before a restored copy replaced it.</summary>
    PreSwap,
}

/// <summary>
/// A logical dump of one tenant's database, held on the control plane's disk.
///
/// <para>
/// <c>pg_dump -Fc</c> of a single database, taken by the panel over its ordinary
/// network connection. That is the whole mechanism: no shell on a database node,
/// no agent, no SSH key — the panel already routes to Postgres and, since the
/// grants were made explicit, can read every tenant it manages.
/// </para>
///
/// <para>
/// This is NOT a replacement for pgBackRest. The two answer different questions.
/// A dump restores ONE tenant to the moment it was taken, which is what almost
/// every real request turns out to be; pgBackRest restores the whole cluster to an
/// arbitrary instant, which is the escape hatch for "put it back to 14:32, just
/// before the bad import".
/// </para>
/// </summary>
public class Snapshot
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? TenantId { get; set; }

    /// <summary>
    /// Copied, not joined. A snapshot's whole purpose is to outlive trouble that
    /// befalls its tenant, including the tenant's own deletion.
    /// </summary>
    [MaxLength(63)]
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>Database it was taken from, which need not match the slug after a swap.</summary>
    [MaxLength(63)]
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>File name within the snapshots directory. Never a path — see SnapshotStore.</summary>
    [MaxLength(200)]
    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public SnapshotKind Kind { get; set; } = SnapshotKind.Manual;

    /// <summary>Free text from the operator, or from the job that took it.</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>Image the tenant was running when this was taken.</summary>
    /// <remarks>
    /// Restoring a dump into a NEWER image is fine — first boot migrates it. The
    /// reverse is not: an older image meets a schema from the future and fails in
    /// ways that look like corruption. Recording the tag is what lets the panel
    /// warn before that happens rather than after.
    /// </remarks>
    [MaxLength(200)]
    public string? ImageTag { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

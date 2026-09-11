namespace ZuloOne.ControlPlane.Jobs;

/// <summary>What a job is doing. The kind decides which handler picks it up.</summary>
public enum JobKind
{
    /// <summary>Build a registered tenant: database → container → ready → admin.</summary>
    Provision,

    /// <summary>
    /// Bring a hand-deployed tenant under management: take over its credentials,
    /// grant the panel access, recreate its container from the registry.
    /// </summary>
    Adopt,

    /// <summary>Logical <c>pg_dump</c> of one tenant, kept as a restore point.</summary>
    Snapshot,

    /// <summary>Rebuild a tenant's data alongside the live one, into a NEW tenant.</summary>
    Restore,

    /// <summary>
    /// Put a verified restored database under the live tenant, keeping the tenant's
    /// identity and renaming the displaced database aside as the undo.
    /// </summary>
    Swap,

    /// <summary>Move a tenant to another image tag, snapshot first.</summary>
    Upgrade,

    /// <summary>Ad-hoc pgBackRest backup of the whole cluster.</summary>
    Backup,

    /// <summary>Hand the Patroni leader role to another node.</summary>
    Switchover,

    /// <summary>
    /// Apply the snapshot retention policy and reconcile the directory against the
    /// registry.
    /// </summary>
    /// <remarks>
    /// A job rather than a bare timer, deliberately. <c>JobWorker</c> runs one job
    /// at a time, and that serialisation is the only thing keeping a delete away
    /// from the <c>pg_dump</c> writing a file or the <c>pg_restore</c> reading one.
    /// </remarks>
    Prune,

    /// <summary>
    /// Dump the panel's own database — the registry — as a restore point.
    /// </summary>
    /// <remarks>
    /// Queued just before the prune on the same maintenance tick, so the fresh
    /// dump exists before retention is applied and the sweep counts it.
    /// </remarks>
    RegistrySnapshot,

    /// <summary>
    /// Moves SEVERAL tenants, one at a time, stopping on the first that fails.
    /// </summary>
    /// <remarks>
    /// One job rather than N, because the wave's whole value is the decision between
    /// tenants: a bad image reaching the second tenant means it must not reach the
    /// twentieth. N independent jobs would each succeed or fail alone and nothing
    /// would be watching the sequence.
    /// </remarks>
    Rollout,

    /// <summary>
    /// Puts models into a tenant that keeps running — no container is recreated.
    /// </summary>
    /// <remarks>
    /// Distinct from Upgrade because the undo is different, and that difference is the
    /// whole reason to name it separately: an Upgrade can be undone by pinning the
    /// previous image back, this cannot, and its snapshot is the only way out.
    /// </remarks>
    InstallModels,
}

/// <summary>
/// Where a job is. Terminal states are Succeeded, Failed and Cancelled.
/// </summary>
public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One long-running operation, recorded so it survives the request that asked for
/// it and the process that ran it.
///
/// <para>
/// Everything the panel does that takes more than a moment goes through this table:
/// provisioning, snapshots, restores, upgrades, backups, switchovers. Before it,
/// provisioning had a bespoke mechanism — a <c>Channel&lt;Guid&gt;</c> plus the
/// tenant's own <c>Status</c> column — and each new operation would have needed its
/// own. Five incomparable mechanisms, and nowhere to look to answer "what is
/// happening right now".
/// </para>
///
/// <para>
/// The row is also the crash record. A job left <see cref="JobState.Running"/> when
/// the service dies is swept to Failed on the next start (see
/// <c>JobWorker.ReapInterruptedAsync</c>), because the alternative is a row that
/// claims to be running forever with nothing behind it.
/// </para>
/// </summary>
public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public JobKind Kind { get; set; }

    /// <summary>The tenant this concerns, when it concerns one.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Copied rather than joined. A Restore or Delete outlives its tenant, and the
    /// history is worth less if the row it points at can vanish and take the name
    /// with it.
    /// </summary>
    public string? TenantSlug { get; set; }

    public JobState State { get; set; } = JobState.Queued;

    /// <summary>Human-readable current step, e.g. "Extracting from the repository".</summary>
    public string? Step { get; set; }

    /// <summary>0..100. Coarse on purpose — these steps are minutes long.</summary>
    public int Progress { get; set; }

    /// <summary>Appended as the job runs; the detail behind <see cref="Step"/>.</summary>
    public string? Log { get; set; }

    /// <summary>Why it failed. Null unless <see cref="State"/> is Failed.</summary>
    public string? Error { get; set; }

    /// <summary>The operator who asked for it, for the history.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Kind-specific arguments as JSON — a target tag for an Upgrade, a point in
    /// time for a Restore. Deliberately opaque here: adding a kind should not mean
    /// adding columns that every other kind leaves null.
    /// </summary>
    public string? Payload { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>
    /// An operator has asked this job to stop. Cooperative: the job reads it and
    /// decides where stopping is safe.
    /// </summary>
    /// <remarks>
    /// A flag rather than a cancellation token because the worker's token is tied to
    /// process shutdown, and this is not that. A wave checks it BETWEEN tenants, so
    /// cancelling never tears a tenant down mid-recreate — the one in flight finishes
    /// or rolls back on its own terms, and the rest are simply not started.
    /// </remarks>
    public bool CancelRequested { get; set; }

    public bool IsTerminal => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;
}

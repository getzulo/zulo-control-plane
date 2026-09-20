using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning.Demo;

/// <summary>
/// Hands a pre-built workspace to whoever asked for one.
/// </summary>
/// <remarks>
/// This is the only place in the demo feature where two callers can collide, so
/// it is the only place that needs to be careful. Everything else — building,
/// reaping, topping the pool up — is done by a single background service.
/// </remarks>
public sealed class DemoPool
{
    private readonly ControlPlaneDbContext _db;

    public DemoPool(ControlPlaneDbContext db) => _db = db;

    /// <summary>
    /// Claims one pooled workspace, or returns null if none was free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim is a single conditional UPDATE — <c>WHERE Id = … AND Demo =
    /// 'Pooled'</c> — and the row count is the answer. Postgres takes a row lock
    /// for the duration of that statement, so of two callers racing for the same
    /// workspace exactly one sees 1 row affected and the other sees 0 and moves
    /// on to the next candidate. Read-then-write would hand the same workspace to
    /// both, and the second visitor would find a stranger already signed in.
    /// </para>
    /// <para>
    /// Deliberately NOT <c>SELECT … FOR UPDATE SKIP LOCKED</c>, which is the
    /// usual answer to this shape. It would be the first raw SQL in the
    /// repository and would need a transaction held open across the read and the
    /// write; a guarded <c>ExecuteUpdateAsync</c> is atomic on its own, stays in
    /// the idiom every other service here uses, and fails the same way.
    /// </para>
    /// <para>
    /// Candidates are re-read on each attempt rather than cached: by the time the
    /// first attempt loses, the list is already stale, and retrying against a
    /// stale list is how a busy pool reports "empty" while holding free
    /// workspaces.
    /// </para>
    /// </remarks>
    /// <param name="lifetimeHours">
    /// Policy, supplied by the caller rather than read here. The pool performs
    /// the atomic claim and nothing else; how long a demo lives is a decision
    /// that belongs with whoever owns <see cref="DemoConfig"/> — and keeping it
    /// out means this class can be exercised against a database without one.
    /// </param>
    public async Task<Tenant?> TryClaimAsync(
        Guid requestId,
        DateTime now,
        int lifetimeHours,
        CancellationToken ct)
    {
        // Bounded so a pathological race cannot spin. Ten losses in a row means
        // ten other callers won ahead of us, and at that point "none free" is the
        // honest answer — the pool is being drained faster than it refills.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidateId = await _db.Tenants
                .Where(t => t.Demo == TenantDemo.Pooled
                            && t.Status == TenantStatus.Active
                            && t.Health == TenantHealth.Ok)
                // Oldest first: a pooled workspace drifts from the template as the
                // golden tenant moves on, so the one closest to being retired is
                // the one to spend.
                .OrderBy(t => t.CreatedAt)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(ct);

            if (candidateId is not { } id)
            {
                return null;
            }

            var claimed = await _db.Tenants
                .Where(t => t.Id == id && t.Demo == TenantDemo.Pooled)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(t => t.Demo, TenantDemo.Claimed)
                        .SetProperty(t => t.ClaimedAt, now)
                        .SetProperty(t => t.ExpiresAt, DemoLifetime.ExpiryForClaimed(now, lifetimeHours))
                        .SetProperty(t => t.DemoRequestId, requestId)
                        .SetProperty(t => t.UpdatedAt, now),
                    ct);

            if (claimed == 1)
            {
                // Read back rather than trusting the in-memory copy: the UPDATE
                // went straight to the database and the tracker never saw it.
                return await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == id, ct);
            }
        }

        return null;
    }

    /// <summary>Pooled workspaces ready to be handed over right now.</summary>
    public Task<int> AvailableAsync(CancellationToken ct) =>
        _db.Tenants.CountAsync(
            t => t.Demo == TenantDemo.Pooled
                 && t.Status == TenantStatus.Active
                 && t.Health == TenantHealth.Ok,
            ct);

    /// <summary>
    /// Every live demo, pooled and claimed together. What <c>Demo:MaxConcurrent</c>
    /// is measured against; the golden tenant is not one of these.
    /// </summary>
    public Task<int> LiveAsync(CancellationToken ct) =>
        _db.Tenants.CountAsync(
            t => t.Demo == TenantDemo.Pooled || t.Demo == TenantDemo.Claimed,
            ct);
}

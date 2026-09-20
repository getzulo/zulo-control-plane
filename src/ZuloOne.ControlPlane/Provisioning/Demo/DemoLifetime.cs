using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning.Demo;

/// <summary>
/// When a demo may be destroyed, and how long one lives.
/// </summary>
/// <remarks>
/// Statics taking a clock rather than reading one, so the reaper's decision can
/// be exercised exhaustively in a test project. The cost of being wrong here is
/// somebody's database, and that is not a thing to find out in production.
/// </remarks>
public static class DemoLifetime
{
    /// <summary>
    /// When an unclaimed demo should be retired. It is capacity while it waits,
    /// and it drifts from the template as the golden tenant moves on.
    /// </summary>
    public static DateTime ExpiryForPooled(DateTime now, int poolMaxAgeHours) =>
        now.AddHours(poolMaxAgeHours);

    /// <summary>
    /// When a claimed demo dies. Measured from the claim, not from the build —
    /// time spent sitting in the pool was never the visitor's.
    /// </summary>
    public static DateTime ExpiryForClaimed(DateTime now, int lifetimeHours) =>
        now.AddHours(lifetimeHours);

    /// <summary>
    /// The reaper's ONLY predicate.
    /// </summary>
    /// <remarks>
    /// Four independent refusals. Every one of them is impossible by
    /// construction, and every one of them is cheap — which is the correct trade
    /// when being wrong means destroying a customer's data. This is the same
    /// reasoning <see cref="TenantOrigin"/> was introduced for on the delete path.
    /// </remarks>
    public static bool IsReapable(
        TenantDemo? demo,
        TenantOrigin origin,
        DateTime? expiresAt,
        DateTime now) =>
        // Never Golden — it is the template's origin — and never a real tenant,
        // whose Demo is null.
        demo is TenantDemo.Pooled or TenantDemo.Claimed
        // Never something the panel did not create. An adopted tenant's data was
        // never ours to destroy.
        && origin == TenantOrigin.Provisioned
        // Never something with no clock on it.
        && expiresAt is { } due
        // And not before its time.
        && due <= now;
}

using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Billing;

/// <summary>
/// What the unpaid-slice sweep (or a pay handler) should do to a stand.
/// </summary>
public enum BillingLicenceAction
{
    None,
    StopUnpaid,
    StartPaid,
}

/// <summary>
/// Pure licence gate for hosting invoices. Stops for overdue, starts when
/// settled — unless the customer pressed Stop themselves.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TenantStatus.Suspended"/> is shared between unpaid collection and
/// customer Stop. <c>StoppedByCustomer</c> keeps them apart: clearing it on a
/// customer-stopped stand, or starting on pay without checking it, turns
/// "I paused my stand" into "the card went through so boot it".
/// </para>
/// <para>
/// Overdue on an already-suspended unpaid stand is a no-op (already collected).
/// Overdue on a customer-stopped stand is also a no-op — do not rewrite their
/// Stop as unpaid Stop.
/// </para>
/// </remarks>
public static class BillingLicence
{
    public static BillingLicenceAction Decide(
        TenantStatus status,
        bool stoppedByCustomer,
        bool demo,
        bool sliceOverdue,
        bool sliceSettled)
    {
        if (demo) return BillingLicenceAction.None;

        // Already Suspended (unpaid or customer) — leave alone. Active (and any
        // other non-Suspended) overdue stands get stopped.
        if (sliceOverdue && status != TenantStatus.Suspended)
            return BillingLicenceAction.StopUnpaid;

        if (sliceSettled && status == TenantStatus.Suspended && !stoppedByCustomer)
            return BillingLicenceAction.StartPaid;

        return BillingLicenceAction.None;
    }
}

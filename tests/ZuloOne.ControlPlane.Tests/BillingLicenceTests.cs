using ZuloOne.ControlPlane.Billing;
using ZuloOne.ControlPlane.Registry;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

/// <summary>
/// The unpaid-slice licence gate: when the sweep (or a pay handler) may Stop
/// or Start a stand. Customer Stop must never be rewritten as unpaid Stop.
/// </summary>
public sealed class BillingLicenceTests
{
    [Theory]
    [InlineData(TenantStatus.Active, false, false, true, false, BillingLicenceAction.StopUnpaid)]
    [InlineData(TenantStatus.Suspended, false, false, true, false, BillingLicenceAction.None)]
    [InlineData(TenantStatus.Suspended, true, false, true, false, BillingLicenceAction.None)]
    [InlineData(TenantStatus.Suspended, false, false, false, true, BillingLicenceAction.StartPaid)]
    [InlineData(TenantStatus.Suspended, true, false, false, true, BillingLicenceAction.None)]
    [InlineData(TenantStatus.Active, false, true, true, false, BillingLicenceAction.None)]
    public void Decide_matches_the_licence_gate(
        TenantStatus status,
        bool stoppedByCustomer,
        bool demo,
        bool sliceOverdue,
        bool sliceSettled,
        BillingLicenceAction expected)
    {
        var action = BillingLicence.Decide(
            status, stoppedByCustomer, demo, sliceOverdue, sliceSettled);

        Assert.Equal(expected, action);
    }
}

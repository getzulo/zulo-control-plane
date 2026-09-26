using ZuloOne.ControlPlane.Portal;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class PortalBillingTests
{
    [Fact]
    public void CheckoutChargeAmount_uses_remaining_after_partial_bank_pay()
    {
        Assert.Equal(40m, PortalBilling.CheckoutChargeAmount(40m, 100m));
    }

    [Fact]
    public void CheckoutChargeAmount_uses_full_remaining_when_unpaid()
    {
        Assert.Equal(100m, PortalBilling.CheckoutChargeAmount(100m, 100m));
    }

    [Fact]
    public void CheckoutChargeAmount_refuses_when_already_paid()
    {
        Assert.Null(PortalBilling.CheckoutChargeAmount(0m, 100m));
        Assert.Null(PortalBilling.CheckoutChargeAmount(-1m, 100m));
    }
}

using ZuloOne.ControlPlane.Provisioning;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class ReservedSlugsBillingTests
{
    [Fact]
    public void Hq_is_reserved_for_the_commercial_stand()
    {
        Assert.True(ReservedSlugs.IsReserved("hq"));
        Assert.True(ReservedSlugs.IsReserved("HQ"));
    }

    [Fact]
    public void Billing_slug_stays_reserved()
    {
        Assert.True(ReservedSlugs.IsReserved("billing"));
    }
}

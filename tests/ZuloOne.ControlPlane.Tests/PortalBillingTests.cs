using System.Text.Json;
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

    [Fact]
    public void StandAppearsOnOverdue_matches_slug_case_insensitively()
    {
        using var doc = JsonDocument.Parse(
            """[{"standSlug":"Acme","invoiceId":"1","remaining":10,"dueDate":"2026-01-01"}]""");
        Assert.True(PortalBilling.StandAppearsOnOverdue(doc.RootElement, "acme"));
        Assert.False(PortalBilling.StandAppearsOnOverdue(doc.RootElement, "other"));
    }

    [Fact]
    public void StandAppearsOnOverdue_empty_array_is_not_overdue()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.False(PortalBilling.StandAppearsOnOverdue(doc.RootElement, "acme"));
    }

    [Fact]
    public void TryFindIssuedUnpaid_uses_currency_from_books()
    {
        using var doc = JsonDocument.Parse(
            """[{"id":"inv-1","subtype":"Issued","remaining":40,"amount":100,"number":"SR-1","currency":"EUR"}]""");
        Assert.True(PortalBilling.TryFindIssuedUnpaid(doc.RootElement, "inv-1", out var amount, out var number, out var currency));
        Assert.Equal(40m, amount);
        Assert.Equal("SR-1", number);
        Assert.Equal("EUR", currency);
    }

    [Fact]
    public void TryFindIssuedUnpaid_refuses_when_currency_missing()
    {
        using var doc = JsonDocument.Parse(
            """[{"id":"inv-1","subtype":"Issued","remaining":40,"amount":100,"number":"SR-1"}]""");
        Assert.False(PortalBilling.TryFindIssuedUnpaid(doc.RootElement, "inv-1", out _, out _, out var currency));
        Assert.Equal("", currency);
    }
}

using System.Security.Cryptography;
using System.Text;
using ZuloOne.ControlPlane.Billing;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

/// <summary>
/// Stripe webhook signature check. The till must reject a forged payload
/// before it posts a CustomerPayment.
/// </summary>
public sealed class StripeWebhookTests
{
    private const string Secret = "whsec_test_fixture_secret";

    [Fact]
    public void VerifyWebhook_accepts_a_known_payload_and_secret()
    {
        var payload = """{"id":"evt_test","type":"checkout.session.completed"}""";
        var timestamp = "1710000000";
        var signature = Sign(timestamp, payload, Secret);
        var header = $"t={timestamp},v1={signature}";

        Assert.True(StripeCheckout.VerifyWebhook(payload, header, Secret));
    }

    [Fact]
    public void VerifyWebhook_rejects_an_invalid_signature()
    {
        var payload = """{"id":"evt_test","type":"checkout.session.completed"}""";
        var header = "t=1710000000,v1=deadbeef";

        Assert.False(StripeCheckout.VerifyWebhook(payload, header, Secret));
    }

    [Fact]
    public void VerifyWebhook_rejects_empty_secret()
    {
        var payload = "{}";
        var header = "t=1,v1=abc";

        Assert.False(StripeCheckout.VerifyWebhook(payload, header, ""));
        Assert.False(StripeCheckout.VerifyWebhook(payload, header, null));
    }

    private static string Sign(string timestamp, string payload, string secret)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, signed);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

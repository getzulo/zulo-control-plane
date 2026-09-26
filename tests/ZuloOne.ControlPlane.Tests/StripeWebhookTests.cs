using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signature = Sign(timestamp, payload, Secret);
        var header = $"t={timestamp},v1={signature}";

        Assert.True(StripeCheckout.VerifyWebhook(payload, header, Secret, utcNow: now));
    }

    [Fact]
    public void VerifyWebhook_rejects_an_invalid_signature()
    {
        var payload = """{"id":"evt_test","type":"checkout.session.completed"}""";
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var header = $"t={timestamp},v1=deadbeef";

        Assert.False(StripeCheckout.VerifyWebhook(payload, header, Secret, utcNow: now));
    }

    [Fact]
    public void VerifyWebhook_rejects_empty_secret()
    {
        var payload = "{}";
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var header = $"t={timestamp},v1=abc";

        Assert.False(StripeCheckout.VerifyWebhook(payload, header, "", utcNow: now));
        Assert.False(StripeCheckout.VerifyWebhook(payload, header, null, utcNow: now));
    }

    [Fact]
    public void VerifyWebhook_rejects_a_stale_timestamp()
    {
        var payload = """{"id":"evt_stale","type":"checkout.session.completed"}""";
        var now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        var stale = now.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var signature = Sign(stale, payload, Secret);
        var header = $"t={stale},v1={signature}";

        Assert.False(StripeCheckout.VerifyWebhook(payload, header, Secret, utcNow: now));
    }

    [Fact]
    public void StripePayBody_passes_through_checkout_amount_total()
    {
        // Checkout billed $42.00 (4200 cents) while remaining receivable may be higher.
        var sessionJson = """
            {
              "id": "cs_test_42",
              "amount_total": 4200,
              "metadata": { "standSlug": "acme", "amount": "99.00" }
            }
            """;
        using var doc = JsonDocument.Parse(sessionJson);
        var amount = StripeCheckout.ChargedAmount(doc.RootElement);

        Assert.Equal(42.00m, amount);

        var body = StripeCheckout.StripePayBody("acme", "cs_test_42", amount);
        using var bodyDoc = JsonDocument.Parse(body);
        Assert.Equal("acme", bodyDoc.RootElement.GetProperty("standSlug").GetString());
        Assert.Equal("cs_test_42", bodyDoc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(42.00m, bodyDoc.RootElement.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public void ChargedAmount_falls_back_to_metadata_when_amount_total_missing()
    {
        var sessionJson = """
            {
              "id": "cs_meta",
              "metadata": { "standSlug": "acme", "amount": "17.50" }
            }
            """;
        using var doc = JsonDocument.Parse(sessionJson);
        Assert.Equal(17.50m, StripeCheckout.ChargedAmount(doc.RootElement));
    }

    private static string Sign(string timestamp, string payload, string secret)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, signed);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

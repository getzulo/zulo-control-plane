using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZuloOne.ControlPlane.Billing;

namespace ZuloOne.ControlPlane.Api;

/// <summary>
/// Stripe's only write into the control plane. Anonymous by necessity — Stripe
/// has no operator session — and guarded by the webhook HMAC, not by the
/// fallback operator policy.
/// </summary>
[ApiController]
[Route("api/billing")]
[Produces("application/json")]
public sealed class StripeWebhookController : ControllerBase
{
    private readonly StripeCheckout _stripe;
    private readonly CommercialBooks _books;
    private readonly BillingConfig _config;
    private readonly BillingLicenceStarter _licence;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        StripeCheckout stripe,
        CommercialBooks books,
        BillingConfig config,
        BillingLicenceStarter licence,
        ILogger<StripeWebhookController> logger)
    {
        _stripe = stripe;
        _books = books;
        _config = config;
        _licence = licence;
        _logger = logger;
    }

    /// <summary>
    /// <c>POST /api/billing/stripe-webhook</c> — verify signature, ignore
    /// unknown types, on <c>checkout.session.completed</c> post stripe-pay.
    /// Always 200 on unknown/duplicate sessions so Stripe does not retry forever.
    /// </summary>
    [HttpPost("stripe-webhook")]
    [AllowAnonymous]
    [EnableRateLimiting(StripeWebhookRateLimit.Policy)]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        string payload;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            payload = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["Stripe-Signature"].ToString();
        if (!_stripe.VerifyWebhook(payload, signature))
        {
            _logger.LogWarning("Rejected Stripe webhook — bad or stale signature");
            return BadRequest(new { error = "Bad Stripe signature." });
        }

        string? eventType = null;
        string? sessionId = null;
        string? standSlug = null;
        decimal? chargedAmount = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
            var root = doc.RootElement;
            eventType = ReadString(root, "type");

            if (TryGetProperty(root, "data", out var data)
                && TryGetProperty(data, "object", out var obj))
            {
                sessionId = ReadString(obj, "id");
                chargedAmount = StripeCheckout.ChargedAmount(obj);
                if (TryGetProperty(obj, "metadata", out var meta))
                    standSlug = ReadString(meta, "standSlug");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Stripe webhook body was not JSON; acknowledging");
            return Ok(new { received = true });
        }

        if (!string.Equals(eventType, "checkout.session.completed", StringComparison.Ordinal))
            return Ok(new { received = true, ignored = eventType });

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(standSlug))
        {
            _logger.LogWarning(
                "Stripe checkout.session.completed missing session id or standSlug metadata; acknowledging");
            return Ok(new { received = true });
        }

        if (!_config.Enabled)
        {
            // Kill switch: acknowledge so Stripe stops retrying, but do not
            // post stripe-pay or Start while billing is off.
            _logger.LogInformation(
                "Stripe pay skipped — Billing:Enabled is false (session {SessionId})",
                sessionId);
            return Ok(new { received = true, skipped = "billing-disabled" });
        }

        if (string.IsNullOrWhiteSpace(_config.TenantSlug))
        {
            _logger.LogWarning("Stripe pay skipped — Billing:TenantSlug is empty");
            return Ok(new { received = true });
        }

        try
        {
            var body = StripeCheckout.StripePayBody(standSlug, sessionId, chargedAmount);
            var paid = await _books.StripePayAsync(body, ct);
            var remaining = ReadDecimal(paid, "remaining");
            var settled = remaining <= 0m
                || string.Equals(ReadString(paid, "status"), "paid", StringComparison.OrdinalIgnoreCase);

            if (settled)
                await _licence.TryStartAfterPayAsync(standSlug, $"Stripe session {sessionId}", ct);
        }
        catch (Exception ex)
        {
            // Unknown stand / books error: log and 200. A 500 makes Stripe retry
            // forever for a session we will never be able to post.
            _logger.LogWarning(ex,
                "Stripe stripe-pay for session {SessionId} stand {StandSlug} failed; acknowledging",
                sessionId, standSlug);
        }

        return Ok(new { received = true });
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) ? value.GetString() : null;

    private static decimal ReadDecimal(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return 0m;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n)) return n;
        return decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value))
            return true;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

/// <summary>Named rate-limit policy for the Stripe webhook (anonymous write).</summary>
public static class StripeWebhookRateLimit
{
    public const string Policy = "stripe-webhook";
}

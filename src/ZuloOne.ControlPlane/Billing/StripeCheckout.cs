using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZuloOne.ControlPlane.Billing;

/// <summary>
/// Stripe Checkout over HTTP — no Stripe.net package. Creates a one-shot
/// payment session for an already Issued realization and verifies webhooks.
/// </summary>
public sealed class StripeCheckout
{
    public const string HttpClientName = "stripe";

    private readonly BillingConfig _config;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<StripeCheckout> _logger;

    public StripeCheckout(
        BillingConfig config,
        IHttpClientFactory http,
        ILogger<StripeCheckout> logger)
    {
        _config = config;
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Whether card pay is wired (secret present). Empty secret hides the button.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.StripeSecretKey);

    /// <summary>
    /// Creates a Checkout Session in <c>payment</c> mode for one Issued invoice.
    /// </summary>
    public async Task<StripeCheckoutSession> CreateSessionAsync(
        string invoiceId,
        string standSlug,
        string invoiceNumber,
        decimal amount,
        string currency,
        string successUrl,
        string cancelUrl,
        CancellationToken ct)
    {
        var secret = _config.StripeSecretKey?.Trim();
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Card payments are not configured.");

        if (amount <= 0m)
            throw new InvalidOperationException("Invoice amount must be positive.");

        var unitAmount = (long)decimal.Round(amount * 100m, MidpointRounding.AwayFromZero);
        if (unitAmount < 1)
            throw new InvalidOperationException("Invoice amount is too small for card payment.");

        if (string.IsNullOrWhiteSpace(currency))
            throw new InvalidOperationException("Invoice currency is required.");

        var currencyCode = currency.Trim().ToLowerInvariant();

        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["success_url"] = successUrl,
            ["cancel_url"] = cancelUrl,
            ["client_reference_id"] = invoiceId,
            ["metadata[invoiceId]"] = invoiceId,
            ["metadata[standSlug]"] = standSlug,
            // Echoed on checkout.session.completed so stripe-pay posts the same
            // sum Checkout charged, not the full remaining receivable.
            ["metadata[amount]"] = amountText,
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = currencyCode,
            ["line_items[0][price_data][unit_amount]"] = unitAmount.ToString(CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] =
                string.IsNullOrWhiteSpace(invoiceNumber) ? invoiceId : invoiceNumber,
        };

        var client = _http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Content = new FormUrlEncodedContent(form);

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Stripe Checkout create failed ({Status}): {Body}",
                (int)response.StatusCode,
                body.Length <= 300 ? body : body[..300] + "…");
            throw new InvalidOperationException("Stripe could not create a checkout session.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sessionId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Stripe returned an incomplete checkout session.");

        return new StripeCheckoutSession(sessionId, url);
    }

    /// <summary>Stripe's default signature tolerance (~5 minutes).</summary>
    public static readonly TimeSpan DefaultWebhookTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verifies <c>Stripe-Signature</c>: HMAC SHA256 of <c>{t}.{payload}</c>
    /// with the webhook secret must match one of the <c>v1=</c> values, and
    /// <c>t=</c> must be within <paramref name="tolerance"/> of
    /// <paramref name="utcNow"/> (Stripe skew / replay guard).
    /// </summary>
    public static bool VerifyWebhook(
        string payload,
        string? signatureHeader,
        string? webhookSecret,
        TimeSpan? tolerance = null,
        DateTimeOffset? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(webhookSecret))
            return false;
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return false;
        if (payload is null)
            return false;

        string? timestamp = null;
        var candidates = new List<string>();
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "t") timestamp = value;
            else if (key == "v1") candidates.Add(value);
        }

        if (string.IsNullOrWhiteSpace(timestamp) || candidates.Count == 0)
            return false;

        if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            return false;

        var eventTime = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var maxSkew = tolerance ?? DefaultWebhookTolerance;
        if (Math.Abs((now - eventTime).TotalSeconds) > maxSkew.TotalSeconds)
            return false;

        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        var keyBytes = Encoding.UTF8.GetBytes(webhookSecret);
        var expected = HMACSHA256.HashData(keyBytes, signed);
        var expectedHex = Convert.ToHexString(expected).ToLowerInvariant();

        foreach (var candidate in candidates)
        {
            if (FixedTimeEqualsHex(expectedHex, candidate))
                return true;
        }

        return false;
    }

    /// <summary>Instance wrapper that uses the bound webhook secret.</summary>
    public bool VerifyWebhook(string payload, string? signatureHeader)
        => VerifyWebhook(payload, signatureHeader, _config.StripeWebhookSecret);

    /// <summary>
    /// Amount Checkout charged, in major currency units. Prefers
    /// <c>amount_total</c> (Stripe cents); falls back to <c>metadata.amount</c>.
    /// </summary>
    public static decimal? ChargedAmount(JsonElement sessionObject)
    {
        if (TryGetProperty(sessionObject, "amount_total", out var total))
        {
            if (total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var cents) && cents > 0)
                return cents / 100m;
            if (total.ValueKind == JsonValueKind.Number && total.TryGetDecimal(out var decCents) && decCents > 0)
                return decCents / 100m;
        }

        if (TryGetProperty(sessionObject, "metadata", out var meta)
            && TryGetProperty(meta, "amount", out var metaAmount))
        {
            if (metaAmount.ValueKind == JsonValueKind.Number && metaAmount.TryGetDecimal(out var n) && n > 0m)
                return n;
            if (decimal.TryParse(metaAmount.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0m)
                return parsed;
        }

        return null;
    }

    /// <summary>
    /// JSON body for books <c>stripe-pay</c>. When <paramref name="amount"/> is
    /// set, books must not default to the full remaining receivable.
    /// </summary>
    public static string StripePayBody(string standSlug, string sessionId, decimal? amount)
    {
        if (amount is > 0m)
        {
            return JsonSerializer.Serialize(new
            {
                standSlug,
                sessionId,
                amount,
            });
        }

        return JsonSerializer.Serialize(new { standSlug, sessionId });
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

    private static bool FixedTimeEqualsHex(string expectedLowerHex, string presented)
    {
        if (string.IsNullOrWhiteSpace(presented))
            return false;

        // Stripe hex is lowercase; accept either case without leaking length
        // differences beyond unequal sizes (which FixedTimeEquals already rejects).
        byte[] a;
        byte[] b;
        try
        {
            a = Convert.FromHexString(expectedLowerHex);
            b = Convert.FromHexString(presented);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed record StripeCheckoutSession(string SessionId, string Url);

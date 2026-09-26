using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Billing;

/// <summary>
/// Talks to the commercial tenant's <c>StandBillingApi</c> web service.
/// </summary>
/// <remarks>
/// The books live in one dedicated fleet tenant — not in the registry. If that
/// tenant is missing or not Active there is no fallback: the panel refuses with
/// a clear 503 rather than inventing a second ledger.
/// </remarks>
public sealed class CommercialBooks
{
    private const string ServiceName = "StandBillingApi";

    private readonly BillingConfig _config;
    private readonly ControlPlaneDbContext _db;
    private readonly TenantApiClient _api;

    public CommercialBooks(BillingConfig config, ControlPlaneDbContext db, TenantApiClient api)
    {
        _config = config;
        _db = db;
        _api = api;
    }

    /// <summary>
    /// Posts <paramref name="action"/> (+ optional JSON body) to the commercial
    /// stand and returns the parsed response.
    /// </summary>
    public async Task<JsonElement> CallAsync(string action, string? bodyJson, CancellationToken ct)
    {
        var slug = _config.TenantSlug;
        var tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug, ct);

        if (tenant is null || tenant.Status != TenantStatus.Active)
            throw new InvalidOperationException("The commercial tenant is down.");

        var (status, body) = await _api.PostRestAsync(
            tenant,
            ServiceName,
            new { Action = action, Body = bodyJson ?? string.Empty },
            TimeSpan.FromMinutes(2),
            ct);

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException(
                $"{ServiceName} returned {status}: {(body.Length <= 300 ? body : body[..300] + "…")}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Issued realizations past due with remaining receivable — JSON array of
    /// <c>{ standSlug, invoiceId, number, dueDate, remaining }</c>.
    /// </summary>
    public async Task<JsonElement> OverdueAsync(CancellationToken ct)
        => await PayloadArrayAsync("overdue", bodyJson: null, ct);

    /// <summary>
    /// Realizations for one stand slug — JSON array; each row carries
    /// <c>status</c> (<c>issued</c>/<c>paid</c>) and <c>remaining</c>.
    /// </summary>
    public async Task<JsonElement> ListForStandAsync(string standSlug, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { standSlug });
        return await PayloadArrayAsync("list", body, ct);
    }

    private async Task<JsonElement> PayloadArrayAsync(string action, string? bodyJson, CancellationToken ct)
    {
        var root = await CallAsync(action, bodyJson, ct);
        if (TryGetProperty(root, "Ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var err = TryGetProperty(root, "Error", out var e) ? e.GetString() : null;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(err) ? $"{ServiceName} {action} failed." : err);
        }

        if (!TryGetProperty(root, "Payload", out var payloadProp))
            return ParseArray("[]");

        var payloadText = payloadProp.ValueKind == JsonValueKind.String
            ? payloadProp.GetString() ?? "[]"
            : payloadProp.GetRawText();

        if (payloadText.Contains("\"error\"", StringComparison.OrdinalIgnoreCase)
            && payloadText.TrimStart().StartsWith('{'))
        {
            using var errDoc = JsonDocument.Parse(payloadText);
            if (TryGetProperty(errDoc.RootElement, "error", out var err))
                throw new InvalidOperationException(err.GetString() ?? $"{ServiceName} {action} failed.");
        }

        return ParseArray(payloadText);
    }

    private static JsonElement ParseArray(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        return doc.RootElement.Clone();
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

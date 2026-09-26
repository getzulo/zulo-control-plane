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
}

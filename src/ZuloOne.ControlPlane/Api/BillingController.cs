using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Billing;
using ZuloOne.ControlPlane.Portal;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

public sealed record IssueInvoiceRequest(
    Guid TenantId,
    decimal Amount,
    string? Currency,
    DateTime PeriodFrom,
    DateTime PeriodTo,
    DateTime DueDate);

public sealed record BankPayRequest(string? StandSlug, decimal? Amount);

/// <summary>
/// Operator front over the commercial tenant's <c>StandBillingApi</c>.
/// </summary>
/// <remarks>
/// No invoice table in the registry — every read and write goes through
/// <see cref="CommercialBooks"/>. Demo stands are refused before the books
/// are called. Void/cancel is omitted in v1: Sales has no Issued→Cancelled
/// transition that reverses Receivable.
/// </remarks>
[ApiController]
[Route("api/billing")]
[Produces("application/json")]
public sealed class BillingController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly CommercialBooks _books;
    private readonly BillingConfig _config;
    private readonly TenantContainerService _containers;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        ControlPlaneDbContext db,
        CommercialBooks books,
        BillingConfig config,
        TenantContainerService containers,
        ILogger<BillingController> logger)
    {
        _db = db;
        _books = books;
        _config = config;
        _containers = containers;
        _logger = logger;
    }

    /// <summary>
    /// Overdue fleet slice, or realizations for one stand.
    /// </summary>
    [HttpGet("invoices")]
    public async Task<IActionResult> Invoices(
        [FromQuery] bool overdue = false,
        [FromQuery] string? standSlug = null,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_config.TenantSlug))
                return BooksDown("Billing:TenantSlug is empty.");

            if (overdue)
            {
                var rows = await _books.OverdueAsync(ct);
                return Ok(NormalizeOverdue(rows));
            }

            if (string.IsNullOrWhiteSpace(standSlug))
                return BadRequest(new { error = "standSlug is required unless overdue=true." });

            var list = await _books.ListForStandAsync(standSlug.Trim(), ct);
            return Ok(NormalizeList(list));
        }
        catch (Exception ex) when (IsBooksDown(ex))
        {
            return BooksDown(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("invoices")]
    public async Task<IActionResult> Issue([FromBody] IssueInvoiceRequest request, CancellationToken ct)
    {
        if (request.Amount <= 0)
            return BadRequest(new { error = "amount must be a positive number." });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == request.TenantId, ct);
        if (tenant is null)
            return NotFound(new { error = "Tenant not found.", id = request.TenantId });

        if (tenant.Demo != null)
            return BadRequest(new { error = "A demo is not invoiced." });

        var email = await OwnerEmailAsync(tenant, ct);
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "No owner e-mail for this stand." });

        var body = JsonSerializer.Serialize(new
        {
            email,
            standSlug = tenant.Slug,
            amount = request.Amount,
            currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim(),
            periodFrom = request.PeriodFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            periodTo = request.PeriodTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            dueDate = request.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });

        try
        {
            if (string.IsNullOrWhiteSpace(_config.TenantSlug))
                return BooksDown("Billing:TenantSlug is empty.");

            var issued = await _books.IssueAsync(body, ct);
            return Ok(JsonSerializer.Deserialize<object>(issued.GetRawText()));
        }
        catch (Exception ex) when (IsBooksDown(ex))
        {
            return BooksDown(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("invoices/{id}/bank-pay")]
    public async Task<IActionResult> BankPay(string id, [FromBody] BankPayRequest? request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { error = "invoice id is required." });

        var standSlug = request?.StandSlug?.Trim();
        if (string.IsNullOrWhiteSpace(standSlug))
            return BadRequest(new { error = "standSlug is required." });

        var payBody = request?.Amount is > 0
            ? JsonSerializer.Serialize(new { standSlug, amount = request.Amount })
            : JsonSerializer.Serialize(new { standSlug });

        try
        {
            if (string.IsNullOrWhiteSpace(_config.TenantSlug))
                return BooksDown("Billing:TenantSlug is empty.");

            var paid = await _books.BankPayAsync(payBody, ct);
            var remaining = ReadDecimal(paid, "remaining");
            var settled = remaining <= 0m
                || string.Equals(ReadString(paid, "status"), "paid", StringComparison.OrdinalIgnoreCase);

            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == standSlug, ct);
            if (tenant is not null && settled)
            {
                var action = BillingLicence.Decide(
                    tenant.Status,
                    tenant.StoppedByCustomer,
                    demo: tenant.Demo != null,
                    sliceOverdue: false,
                    sliceSettled: true);

                if (action == BillingLicenceAction.StartPaid)
                {
                    if (string.IsNullOrWhiteSpace(tenant.ContainerId))
                    {
                        _logger.LogWarning(
                            "Bank pay settled {Slug} but StartPaid skipped: no container", tenant.Slug);
                    }
                    else
                    {
                        await _containers.StartAsync(tenant.ContainerId!, ct);
                        tenant.Status = TenantStatus.Active;
                        tenant.LastError = null;
                        tenant.UpdatedAt = DateTime.UtcNow;
                        await _db.SaveChangesAsync(ct);
                        _logger.LogInformation(
                            "Started paid stand {Slug} after bank pay of invoice {InvoiceId}",
                            tenant.Slug, id);
                    }
                }
            }

            return Ok(JsonSerializer.Deserialize<object>(paid.GetRawText()));
        }
        catch (Exception ex) when (IsBooksDown(ex))
        {
            return BooksDown(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task<string?> OwnerEmailAsync(Tenant tenant, CancellationToken ct)
    {
        var owner = await _db.TenantMemberships
            .AsNoTracking()
            .Include(m => m.Account)
            .Where(m => m.TenantId == tenant.Id && m.Role == MembershipRole.Owner)
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(owner?.Account?.Email))
            return owner.Account.Email;

        return string.IsNullOrWhiteSpace(tenant.AdminEmail) ? null : tenant.AdminEmail;
    }

    private static object NormalizeOverdue(JsonElement rows)
    {
        var list = new List<object>();
        if (rows.ValueKind != JsonValueKind.Array) return list;

        foreach (var row in rows.EnumerateArray())
        {
            var id = ReadString(row, "invoiceId") ?? ReadString(row, "id") ?? "";
            list.Add(new
            {
                id,
                number = ReadString(row, "number"),
                standSlug = ReadString(row, "standSlug"),
                dueDate = ReadString(row, "dueDate"),
                remaining = ReadDecimal(row, "remaining"),
                status = "issued",
                amount = (decimal?)null,
                periodFrom = (string?)null,
                periodTo = (string?)null,
            });
        }

        return list;
    }

    private static object NormalizeList(JsonElement rows)
    {
        var list = new List<object>();
        if (rows.ValueKind != JsonValueKind.Array) return list;

        foreach (var row in rows.EnumerateArray())
        {
            list.Add(new
            {
                id = ReadString(row, "id") ?? "",
                number = ReadString(row, "number"),
                standSlug = ReadString(row, "standSlug"),
                dueDate = ReadString(row, "dueDate"),
                remaining = ReadDecimal(row, "remaining"),
                status = ReadString(row, "status") ?? "issued",
                amount = ReadDecimal(row, "amount"),
                periodFrom = ReadString(row, "periodFrom"),
                periodTo = ReadString(row, "periodTo"),
                subtype = ReadString(row, "subtype"),
            });
        }

        return list;
    }

    private static bool IsBooksDown(Exception ex)
        => ex.Message.Contains("The commercial tenant is down.", StringComparison.Ordinal);

    private ObjectResult BooksDown(string message)
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = message });

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

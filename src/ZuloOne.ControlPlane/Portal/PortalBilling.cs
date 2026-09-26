using System.Globalization;
using System.Text.Json;

namespace ZuloOne.ControlPlane.Portal;

/// <summary>
/// Portal billing helpers kept out of <see cref="PortalController"/> so the
/// controller does not keep growing for invoice list / checkout plumbing.
/// </summary>
public static class PortalBilling
{
    /// <summary>
    /// Where Stripe returns the customer after Checkout — the cabinet stand page.
    /// Built from configured <see cref="PortalSettings.PublicUrl"/>, never Host.
    /// </summary>
    public static string? CabinetStandUrl(PortalSettings settings, Guid tenantId)
    {
        var root = (settings.PublicUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(root)) return null;

        var locale = string.IsNullOrWhiteSpace(settings.DefaultLocale) ? "en" : settings.DefaultLocale;
        // getzulo.com cabinet: /{locale}/cabinet — stand detail is client state today;
        // returning to the cabinet is enough for the customer to refresh invoices.
        return $"{root}/{locale}/cabinet?stand={tenantId:D}";
    }

    /// <summary>
    /// Issued (and paid-while-Issued) rows only — Draft stays in the commercial ERP.
    /// </summary>
    public static List<object> CustomerVisibleInvoices(JsonElement rows)
    {
        var list = new List<object>();
        if (rows.ValueKind != JsonValueKind.Array) return list;

        foreach (var row in rows.EnumerateArray())
        {
            if (!IsIssuedDocument(row)) continue;

            list.Add(new
            {
                id = ReadString(row, "id") ?? "",
                number = ReadString(row, "number"),
                standSlug = ReadString(row, "standSlug"),
                dueDate = ReadString(row, "dueDate"),
                remaining = ReadDecimal(row, "remaining"),
                status = ReadString(row, "status") ?? "issued",
                amount = ReadDecimal(row, "amount"),
                currency = ReadString(row, "currency"),
                periodFrom = ReadString(row, "periodFrom"),
                periodTo = ReadString(row, "periodTo"),
            });
        }

        return list;
    }

    /// <summary>
    /// Amount Stripe Checkout should bill: the books receivable slice when
    /// <paramref name="remaining"/> is positive, otherwise the invoice total.
    /// Returns null when nothing is left to pay (<c>remaining &lt;= 0</c>).
    /// </summary>
    public static decimal? CheckoutChargeAmount(decimal remaining, decimal invoiceAmount)
    {
        if (remaining <= 0m)
            return null;
        return remaining > 0m ? remaining : invoiceAmount > 0m ? invoiceAmount : null;
    }

    /// <summary>
    /// True when the overdue array from the books still lists this stand.
    /// Used by cabinet Start — refuse while receivable is past due.
    /// </summary>
    public static bool StandAppearsOnOverdue(JsonElement overdueRows, string standSlug)
    {
        if (string.IsNullOrWhiteSpace(standSlug)) return false;
        if (overdueRows.ValueKind != JsonValueKind.Array) return false;

        foreach (var row in overdueRows.EnumerateArray())
        {
            var slug = ReadString(row, "standSlug");
            if (string.Equals(slug, standSlug, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds one Issued unpaid invoice for Checkout. Charges
    /// <see cref="CheckoutChargeAmount"/> (remaining receivable), not the
    /// original invoice total after a partial bank payment. Requires a
    /// non-empty currency ISO from the books — never invents <c>usd</c>.
    /// </summary>
    public static bool TryFindIssuedUnpaid(
        JsonElement rows,
        string invoiceId,
        out decimal amount,
        out string? number,
        out string currency)
    {
        amount = 0m;
        number = null;
        currency = "";
        if (rows.ValueKind != JsonValueKind.Array) return false;

        foreach (var row in rows.EnumerateArray())
        {
            var id = ReadString(row, "id");
            if (!string.Equals(id, invoiceId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsIssuedDocument(row)) return false;

            var remaining = ReadDecimal(row, "remaining");
            var charge = CheckoutChargeAmount(remaining, ReadDecimal(row, "amount"));
            if (charge is not > 0m) return false;

            var cur = ReadString(row, "currency");
            if (string.IsNullOrWhiteSpace(cur)) return false;

            amount = charge.Value;
            number = ReadString(row, "number");
            currency = cur.Trim();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Draft realizations stay in the commercial ERP. Paid is not a subtype —
    /// status comes from remaining Receivable while subtype stays Issued.
    /// </summary>
    private static bool IsIssuedDocument(JsonElement row)
    {
        var subtype = ReadString(row, "subtype");
        if (string.Equals(subtype, "Draft", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(subtype, "Issued", StringComparison.OrdinalIgnoreCase);
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

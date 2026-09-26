using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Billing;

/// <summary>
/// Asks the commercial books who is overdue / settled and Stops or Starts the
/// matching stands. Same interval class as the demo pool.
/// </summary>
public sealed class BillingSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BillingConfig _config;
    private readonly ILogger<BillingSweepService> _logger;

    public BillingSweepService(
        IServiceScopeFactory scopes,
        BillingConfig config,
        ILogger<BillingSweepService> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_config.Enabled)
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failed sweep is retried on the next tick. Throwing from a
                    // BackgroundService would stop the whole control plane.
                    _logger.LogError(ex, "Billing sweep failed");
                }
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(15, _config.SweepIntervalSeconds)),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var books = scope.ServiceProvider.GetRequiredService<CommercialBooks>();
        var containers = scope.ServiceProvider.GetRequiredService<TenantContainerService>();

        var overdue = await books.OverdueAsync(ct);
        var overdueSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in overdue.EnumerateArray())
        {
            var slug = ReadString(row, "standSlug");
            if (string.IsNullOrWhiteSpace(slug)) continue;
            overdueSlugs.Add(slug);

            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
            if (tenant is null) continue;

            await ApplyAsync(
                tenant, containers, db,
                sliceOverdue: true, sliceSettled: false, ct);
        }

        // Settled stands no longer on the overdue list may be started — unpaid
        // Stop only. Customer-stopped stands stay down.
        var candidates = await db.Tenants
            .Where(t => t.Status == TenantStatus.Suspended
                        && !t.StoppedByCustomer
                        && t.Demo == null)
            .ToListAsync(ct);

        foreach (var tenant in candidates)
        {
            if (overdueSlugs.Contains(tenant.Slug)) continue;

            bool settled;
            try
            {
                var list = await books.ListForStandAsync(tenant.Slug, ct);
                settled = IsSettled(list);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not list invoices for {Slug}; skipping StartPaid check", tenant.Slug);
                continue;
            }

            if (!settled) continue;

            await ApplyAsync(
                tenant, containers, db,
                sliceOverdue: false, sliceSettled: true, ct);
        }
    }

    private async Task ApplyAsync(
        Tenant tenant,
        TenantContainerService containers,
        ControlPlaneDbContext db,
        bool sliceOverdue,
        bool sliceSettled,
        CancellationToken ct)
    {
        var action = BillingLicence.Decide(
            tenant.Status,
            tenant.StoppedByCustomer,
            demo: tenant.Demo != null,
            sliceOverdue,
            sliceSettled);

        if (action == BillingLicenceAction.None) return;

        if (string.IsNullOrWhiteSpace(tenant.ContainerId))
        {
            _logger.LogWarning("Billing licence {Action} skipped for {Slug}: no container", action, tenant.Slug);
            return;
        }

        try
        {
            switch (action)
            {
                case BillingLicenceAction.StopUnpaid:
                    await containers.StopAsync(tenant.ContainerId!, ct);
                    tenant.Status = TenantStatus.Suspended;
                    tenant.Health = TenantHealth.Down;
                    tenant.StoppedByCustomer = false;
                    tenant.LastError = null;
                    tenant.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Stopped unpaid stand {Slug}", tenant.Slug);
                    break;

                case BillingLicenceAction.StartPaid:
                    await containers.StartAsync(tenant.ContainerId!, ct);
                    tenant.Status = TenantStatus.Active;
                    tenant.LastError = null;
                    tenant.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Started paid stand {Slug}", tenant.Slug);
                    break;

                case BillingLicenceAction.None:
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected billing licence action {action}.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Billing licence {Action} failed for {Slug}", action, tenant.Slug);
        }
    }

    private static bool IsSettled(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return false;

        var first = list[0];
        if (TryGetProperty(first, "remaining", out var rem)
            && rem.TryGetDecimal(out var amount)
            && amount <= 0m)
            return true;

        var status = ReadString(first, "status");
        return string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) ? value.GetString() : null;

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

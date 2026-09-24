using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning.Demo;

/// <summary>Reaps expired demos, serves the queue and keeps the warm pool full.</summary>
public sealed class DemoPoolService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DemoConfig _config;
    private readonly DemoNudge _nudge;
    private readonly ILogger<DemoPoolService> _logger;

    public DemoPoolService(
        IServiceScopeFactory scopes,
        DemoConfig config,
        DemoNudge nudge,
        ILogger<DemoPoolService> logger)
    {
        _scopes = scopes;
        _config = config;
        _nudge = nudge;
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
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failed sweep is retried on the next tick. Throwing from a
                    // BackgroundService would stop the whole control plane.
                    _logger.LogError(ex, "Demo pool reconciliation failed");
                }
            }

            await _nudge.WaitAsync(
                TimeSpan.FromSeconds(Math.Max(15, _config.ReapIntervalSeconds)),
                stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var provisioner = scope.ServiceProvider.GetRequiredService<TenantProvisioner>();
        var pool = scope.ServiceProvider.GetRequiredService<DemoPool>();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var now = DateTime.UtcNow;

        var expired = await db.Tenants
            .Where(t => (t.Demo == TenantDemo.Pooled || t.Demo == TenantDemo.Claimed)
                        && t.Origin == TenantOrigin.Provisioned
                        && t.ExpiresAt != null
                        && t.ExpiresAt <= now
                        && t.Status != TenantStatus.Deleting)
            .OrderBy(t => t.ExpiresAt)
            .ToListAsync(ct);

        foreach (var tenant in expired)
        {
            try
            {
                await provisioner.DeleteAsync(tenant, ct);
                _logger.LogInformation("Reaped expired demo {Slug}", tenant.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not reap demo {Slug}", tenant.Slug);
            }
        }

        // Oldest request first. Claim and request update share one registry
        // database; the tenant claim itself is the atomic collision boundary.
        while (true)
        {
            var request = await db.DemoRequests
                .Where(r => r.State == DemoRequestState.Queued)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (request is null) break;

            var tenant = await pool.TryClaimAsync(request.Id, now, _config.LifetimeHours, ct);
            if (tenant is null) break;

            request.State = DemoRequestState.Ready;
            request.TenantId = tenant.Id;
            request.TenantSlug = tenant.Slug;
            request.ReadyAt = now;
            request.Note = null;
            await db.SaveChangesAsync(ct);
        }

        var available = await pool.AvailableAsync(ct);
        var live = await pool.LiveAsync(ct);
        var scheduled = await db.Jobs.CountAsync(
            j => j.Kind == JobKind.DemoProvision
                 && (j.State == JobState.Queued || j.State == JobState.Running), ct);

        var builds = CalculateBuildCount(
            available, scheduled, live, _config.PoolTarget, _config.MaxConcurrent);
        if (_config.TemplateSnapshotId is null) builds = 0;

        for (var i = 0; i < builds; i++)
        {
            await queue.EnqueueAsync(
                JobKind.DemoProvision, tenantId: null, tenantSlug: null,
                createdBy: "system:demo-pool", ct: ct);
        }
    }

    public static int CalculateBuildCount(
        int available,
        int scheduled,
        int live,
        int poolTarget,
        int maxConcurrent)
    {
        var poolGap = Math.Max(0, poolTarget - available - scheduled);
        var capacity = Math.Max(0, maxConcurrent - live - scheduled);
        return Math.Min(poolGap, capacity);
    }
}

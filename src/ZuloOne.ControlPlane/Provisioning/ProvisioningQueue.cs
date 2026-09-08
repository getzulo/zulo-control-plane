using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Carries a registered tenant from "row exists" to "running", off the request
/// that asked for it.
///
/// <para>
/// Provisioning takes minutes — a database, a container, then a wait for first
/// boot to finish migrations, schema sync and a metadata compile. It used to run
/// inline on <c>POST /api/tenants</c>, which made the work depend on the caller
/// staying connected. A closed tab or a proxy's idle timeout cancelled it halfway
/// and left a live container with an empty user table on a public hostname, with
/// nothing in the service that would ever revisit it.
/// </para>
///
/// <para>
/// A channel rather than <c>Task.Run</c>: fire-and-forget swallows exceptions,
/// dies silently on shutdown, and gives no way to bound concurrency. Here the work
/// has a named owner, its own scope, and a token tied to the host's lifetime
/// instead of a request's.
/// </para>
/// </summary>
public interface IProvisioningQueue
{
    /// <summary>Hands a registered tenant to the worker. Never blocks.</summary>
    void Enqueue(Guid tenantId);
}

public sealed class ProvisioningQueue : IProvisioningQueue
{
    // Unbounded is safe here because the only producer is an authenticated
    // operator creating tenants by hand; there is no path that can flood it.
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid tenantId) => _channel.Writer.TryWrite(tenantId);
}

/// <summary>
/// Drains <see cref="ProvisioningQueue"/>, one tenant at a time.
/// </summary>
public sealed class ProvisioningWorker : BackgroundService
{
    private readonly ProvisioningQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ProvisioningWorker> _logger;

    public ProvisioningWorker(ProvisioningQueue queue, IServiceScopeFactory scopes, ILogger<ProvisioningWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var tenantId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Serial on purpose. Two tenants building at once means two first-boot
            // metadata compiles competing for the same host's CPU, which makes both
            // slower and can push either past the readiness timeout.
            try
            {
                // A scope per tenant: ControlPlaneDbContext is scoped, and a single
                // long-lived context would accumulate tracked entities across every
                // tenant the service ever builds.
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                var provisioner = scope.ServiceProvider.GetRequiredService<TenantProvisioner>();

                var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, stoppingToken);
                if (tenant is null)
                {
                    _logger.LogWarning("Tenant {TenantId} was queued but is no longer in the registry", tenantId);
                    continue;
                }

                await provisioner.BuildAsync(tenant, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The service is shutting down mid-build. The row stays
                // Provisioning and the container may exist; §A2's rollback cannot
                // help here because the process is going away. Say so plainly —
                // this is the one case that still needs a human.
                _logger.LogWarning(
                    "Shutting down while building tenant {TenantId} — it may be left half-built; check for an orphan container",
                    tenantId);
                throw;
            }
            catch (Exception ex)
            {
                // BuildAsync already recorded Failed and rolled back. Log and keep
                // draining: one tenant's failure must not stop the queue.
                _logger.LogError(ex, "Building tenant {TenantId} failed", tenantId);
            }
        }
    }
}

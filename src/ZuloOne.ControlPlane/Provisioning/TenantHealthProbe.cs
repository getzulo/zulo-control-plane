using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Asks a tenant whether it is ready, via the platform's <c>/health</c> probe.
/// Requests go through Traefik by Host header rather than to the container
/// directly — that way readiness means "reachable the way a customer reaches it",
/// not merely "the process started".
/// </summary>
public sealed class TenantHealthProbe
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantHealthProbe> _logger;

    public TenantHealthProbe(IHttpClientFactory httpClientFactory, ILogger<TenantHealthProbe> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> IsHealthyAsync(string host, CancellationToken ct = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("tenant");
            using var response = await client.GetAsync($"http://{host}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Connection refused IS the answer while a tenant is still booting —
            // Kestrel does not bind until startup initialization finishes.
            return false;
        }
    }

    /// <summary>Polls until the tenant reports ready or the budget runs out.</summary>
    public async Task<bool> WaitUntilReadyAsync(string host, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await IsHealthyAsync(host, ct))
            {
                _logger.LogInformation("Tenant {Host} reported ready after {Attempts} probe(s)", host, attempt + 1);
                return true;
            }

            attempt++;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        _logger.LogWarning("Tenant {Host} never reported ready", host);
        return false;
    }

    /// <summary>Refreshes the stored health of one tenant.</summary>
    public async Task RefreshAsync(Tenant tenant, string host, CancellationToken ct = default)
    {
        tenant.Health = await IsHealthyAsync(host, ct) ? TenantHealth.Ok : TenantHealth.Down;
        tenant.LastHealthAt = DateTime.UtcNow;
    }
}

using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Runs, stops and removes tenant containers, and stamps them with the Traefik
/// labels that publish the subdomain. The container publishes NO ports: Traefik
/// reaches it over the shared edge network, so a tenant is never addressable
/// except through the proxy (§5).
/// </summary>
public sealed class TenantContainerService
{
    private readonly IDockerClient _docker;
    private readonly FleetSettings _fleet;
    private readonly ILogger<TenantContainerService> _logger;

    public TenantContainerService(IDockerClient docker, IOptions<FleetSettings> fleet, ILogger<TenantContainerService> logger)
    {
        _docker = docker;
        _fleet = fleet.Value;
        _logger = logger;
    }

    public string HostFor(string slug) => $"{slug}.{_fleet.RootDomain}";

    /// <summary>Creates and starts the tenant's container; returns its id.</summary>
    public async Task<string> RunAsync(Tenant tenant, string connectionString, CancellationToken ct = default)
    {
        var name = $"zuloone-tenant-{tenant.Slug}";
        var host = HostFor(tenant.Slug);
        var router = $"tenant-{tenant.Slug}";

        var labels = new Dictionary<string, string>
        {
            ["traefik.enable"] = "true",
            ["traefik.docker.network"] = _fleet.EdgeNetwork,
            [$"traefik.http.routers.{router}.rule"] = $"Host(`{host}`)",
            [$"traefik.http.routers.{router}.entrypoints"] = _fleet.TraefikEntrypoint,
            [$"traefik.http.services.{router}.loadbalancer.server.port"] = _fleet.ContainerPort.ToString(),
            // Ties the container back to the registry row, so an orphan can be
            // recognised as a tenant rather than guessed at by name.
            ["zuloone.tenant.id"] = tenant.Id.ToString(),
            ["zuloone.tenant.slug"] = tenant.Slug,
        };
        if (!string.IsNullOrWhiteSpace(_fleet.TraefikCertResolver))
        {
            labels[$"traefik.http.routers.{router}.tls"] = "true";
            labels[$"traefik.http.routers.{router}.tls.certresolver"] = _fleet.TraefikCertResolver;
        }

        var env = new List<string>
        {
            "Database__Provider=PostgreSql",
            $"ConnectionStrings__DefaultConnection={connectionString}",
            $"Jwt__SigningKey={tenant.JwtSigningKey}",
            $"ASPNETCORE_URLS=http://+:{_fleet.ContainerPort}",
            "ASPNETCORE_ENVIRONMENT=Production",
            $"ZuloOne__BehindReverseProxy={_fleet.BehindReverseProxy.ToString().ToLowerInvariant()}",
            $"ZuloOne__PublicUrl=https://{host}",
        };

        // Remove a stale container of the same name first — provisioning must be
        // retryable after a failure, not blocked by its own leftovers.
        await RemoveAsync(name, ct);

        var created = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = tenant.ImageTag,
            Env = env,
            Labels = labels,
            HostConfig = new HostConfig
            {
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                Memory = _fleet.MemoryLimitBytes,
                NanoCPUs = _fleet.CpuLimit > 0 ? (long)(_fleet.CpuLimit * 1_000_000_000m) : 0,
                NetworkMode = _fleet.EdgeNetwork,
            },
        }, ct);

        // A second network cannot be given at creation time — attach it after.
        if (!string.IsNullOrWhiteSpace(_fleet.DataNetwork))
        {
            await _docker.Networks.ConnectNetworkAsync(_fleet.DataNetwork,
                new NetworkConnectParameters { Container = created.ID }, ct);
        }

        await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        _logger.LogInformation("Started container {Container} for tenant {Slug} at {Host}", created.ID[..12], tenant.Slug, host);
        return created.ID;
    }

    public async Task StopAsync(string containerId, CancellationToken ct = default)
        => await _docker.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 30 }, ct);

    public async Task StartAsync(string containerId, CancellationToken ct = default)
        => await _docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

    public async Task RestartAsync(string containerId, CancellationToken ct = default)
        => await _docker.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters { WaitBeforeKillSeconds = 30 }, ct);

    /// <summary>Removes a container by id or name; missing is success, not an error.</summary>
    public async Task RemoveAsync(string idOrName, CancellationToken ct = default)
    {
        try
        {
            await _docker.Containers.RemoveContainerAsync(idOrName,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = false }, ct);
        }
        catch (DockerContainerNotFoundException) { /* already gone */ }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }

    /// <summary>Recent stdout/stderr, for the per-tenant drawer in the dashboard.</summary>
    public async Task<string> TailLogsAsync(string containerId, int lines = 200, CancellationToken ct = default)
    {
        using var stream = await _docker.Containers.GetContainerLogsAsync(containerId, tty: false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = lines.ToString() }, ct);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
        return string.Concat(stdout, stderr);
    }
}

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
    private readonly FleetConfig _fleet;
    private readonly ILogger<TenantContainerService> _logger;

    public TenantContainerService(IDockerClient docker, FleetConfig fleet, ILogger<TenantContainerService> logger)
    {
        _docker = docker;
        _fleet = fleet;
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
            // ALWAYS set, independently of the resolver below. The router is bound
            // to the `websecure` entrypoint, and a router there without tls=true
            // does not match TLS traffic at all — the edge answers 404, the health
            // probe never succeeds, and provisioning dies on the readiness timeout
            // minutes later. Nothing in that chain mentions a missing label.
            //
            // Production deliberately has NO cert resolver: the certificate is a
            // Cloudflare Origin CA wildcard served from Traefik's default TLS store
            // (traefik/dynamic/tls.yml). Gating tls=true on the resolver therefore
            // disabled it exactly where it was needed, while the hand-deployed
            // tenant kept working because its compose file sets the label directly.
            [$"traefik.http.routers.{router}.tls"] = "true",
        };
        // Only when ACME is actually in use. With the file-provider store there is
        // nothing to resolve, and naming a resolver that does not exist makes
        // Traefik reject the router.
        if (!string.IsNullOrWhiteSpace(_fleet.TraefikCertResolver))
        {
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
            // Which business-layer models this tenant installs from the bundles its
            // image carries. Read on every boot by PackageInstaller, so an upgrade
            // to an image with newer models applies them without anything else
            // happening — and a tenant that bought accounting does not silently
            // acquire payroll because it shipped in the same file.
            $"ZuloOne__Packages__Install={_fleet.Packages}",
        };

        // Remove a stale container of the same name first — provisioning must be
        // retryable after a failure, not blocked by its own leftovers.
        await RemoveAsync(name, ct);

        // The image must be on THIS daemon. CreateContainerAsync does not pull, and
        // the failure is a bare
        //   Docker API responded with status code=NotFound, No such image: …
        // AFTER the old container has already been removed — which is how an
        // upgrade to a freshly published tag took a live tenant down instead of
        // failing before touching it.
        await EnsureImageAsync(tenant.ImageTag, ct);

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
                // The data-protection key ring, on a named volume per tenant.
                //
                // Without it the ring lives in the container's own filesystem, and
                // every recreate mints a new one — which silently makes everything
                // the tenant encrypted with the old ring unreadable. Its stored
                // assistant API keys are exactly that. The failure surfaces long
                // after the change that caused it, as "the key I saved stopped
                // working", with an upgrade somewhere in the history.
                //
                // Docker creates the volume on first use, so this needs no
                // provisioning step; it is deliberately NOT removed with the
                // container, only with the tenant.
                Binds = [$"zuloone-dp-{tenant.Slug}:/var/zuloone/dp-keys"],
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

    /// <summary>
    /// Finds a running or stopped container by exact name, or null.
    ///
    /// Used when adopting a tenant the panel did not create: its container id has to
    /// come from the daemon, because nothing in the registry has ever recorded it.
    /// </summary>
    public async Task<(string Id, string Image)?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [name] = true },
            },
        }, ct);

        // Docker's name filter is a SUBSTRING match, so "zuloone-tenant-t1" also
        // returns "zuloone-tenant-t100". Compare exactly; names carry a leading '/'.
        var match = containers.FirstOrDefault(c =>
            c.Names.Any(n => string.Equals(n.TrimStart('/'), name, StringComparison.Ordinal)));
        return match is null ? null : (match.ID, match.Image);
    }

    /// <summary>
    /// Makes sure the image is on this daemon, pulling it if not.
    ///
    /// <para>
    /// Inspect FIRST, pull only when missing — not pull-always. Tenants pin
    /// immutable release tags, so a tag that is already here is by definition the
    /// right content; pulling anyway would add a registry round trip to every
    /// container start and would make the registry being down enough to stop a
    /// tenant that needs nothing from it.
    /// </para>
    /// </summary>
    private async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await _docker.Images.InspectImageAsync(image, ct);
            return;
        }
        catch (DockerImageNotFoundException) { /* fall through and pull */ }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }

        // `repo:tag`, splitting on the LAST colon — the registry host carries one
        // too (10.10.0.210:5000/zuloone-core:2026.0.30), and splitting on the first
        // would try to pull a tag called "5000/zuloone-core".
        var colon = image.LastIndexOf(':');
        var slash = image.LastIndexOf('/');
        var (repo, tag) = colon > slash
            ? (image[..colon], image[(colon + 1)..])
            : (image, "latest");

        _logger.LogInformation("Pulling {Image} — not present on this daemon", image);
        await _docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag },
            authConfig: null,
            // The daemon streams progress; nothing here needs it, but the overload
            // requires a sink and a null one throws.
            progress: new Progress<JSONMessage>(),
            cancellationToken: ct);
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

    /// <summary>
    /// Removes the tenant's data-protection volume.
    ///
    /// Deliberately separate from removing the container: a container is removed on
    /// every upgrade, and taking the key ring with it would make everything the
    /// tenant encrypted unreadable. Only destroying the TENANT destroys the ring.
    /// </summary>
    public async Task RemoveVolumeAsync(string slug, CancellationToken ct = default)
    {
        try
        {
            await _docker.Volumes.RemoveAsync($"zuloone-dp-{slug}", force: true, ct);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Never created, or already gone. Both are the desired end state.
        }
        catch (Exception ex)
        {
            // Not worth failing a teardown over: the database and the container are
            // what hold the data, and an orphaned volume is a few kilobytes of keys.
            _logger.LogWarning(ex, "Could not remove the data-protection volume for {Slug}", slug);
        }
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

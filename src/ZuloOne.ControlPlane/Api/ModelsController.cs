using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

/// <summary>
/// Which models exist, at which versions, and what each tenant actually runs.
/// </summary>
/// <remarks>
/// <para>
/// Two sources, deliberately, because they answer different questions and disagreeing
/// is the interesting case. The REGISTRY says what an image would install — read from
/// its labels, so no image has to be pulled or booted. Each TENANT'S OWN DATABASE says
/// what is installed — the registry row records only the tag a tenant is pinned to,
/// never what that tag put in.
/// </para>
///
/// <para>
/// That gap has been wrong in both directions this month: a tenant whose image carried
/// no business layer at all, and a package row claiming a version whose content never
/// arrived. Putting the two side by side is the point of this screen.
/// </para>
/// </remarks>
[ApiController]
[Route("api/models")]
[Produces("application/json")]
public class ModelsController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly RegistryModelCatalog _catalogue;
    private readonly TenantModelsService _tenantModels;
    private readonly ImageTreeReader _trees;
    private readonly FleetConfig _fleet;
    private readonly IJobQueue _queue;

    public ModelsController(
        ControlPlaneDbContext db,
        RegistryModelCatalog catalogue,
        TenantModelsService tenantModels,
        ImageTreeReader trees,
        FleetConfig fleet,
        IJobQueue queue)
    {
        _db = db;
        _catalogue = catalogue;
        _tenantModels = tenantModels;
        _trees = trees;
        _fleet = fleet;
        _queue = queue;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var (registry, _) = ImagesController.SplitImage(_fleet.DefaultImage);
        if (registry is null)
        {
            return Ok(new
            {
                error = "Fleet:DefaultImage does not name a registry, so the catalogue cannot be read.",
                models = Array.Empty<object>(),
                images = Array.Empty<object>(),
                tenants = Array.Empty<object>(),
            });
        }

        var images = await _catalogue.ReadAsync(registry, ct);
        var sourceImage = PickDistribution(images);
        var graphNodes = sourceImage is null
            ? []
            : await _trees.ReadGraphAsync(sourceImage, ct);
        var graph = graphNodes.ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);

        // model → version → the tags that carry it. Sorted newest-looking first so the
        // row reads as "this is current, these are the older ones still available".
        var models = images
            .SelectMany(i => i.Models.Select(m => (i, m)))
            .Where(x => !StandModel.IsShippedName(x.m.Name))
            .GroupBy(x => x.m.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g
                    .Select(x => x.m.Version)
                    .OrderByDescending(v => v, Comparer<string>.Create(ModelGraph.CompareVersions))
                    .First();
                graph.TryGetValue(g.Key, out var node);
                return new
                {
                    model = g.Key,
                    latest,
                    isSystem = node?.IsSystem ?? false,
                    dependsOn = node?.DependsOn ?? [],
                    extends = node?.Extends ?? [],
                    versions = g
                        .GroupBy(x => x.m.Version, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(v => v.Key, Comparer<string>.Create(ModelGraph.CompareVersions))
                        .Select(v => new
                        {
                            version = v.Key,
                            images = v.Select(x => x.i.Image).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList(),
                        })
                        .ToList(),
                };
            })
            .ToList();
        var latestByName = models.ToDictionary(m => m.model, m => m.latest, StringComparer.OrdinalIgnoreCase);

        var tenants = new List<object>();
        foreach (var tenant in await _db.Tenants.AsNoTracking().OrderBy(t => t.Slug).ToListAsync(ct))
        {
            // Per tenant, and failures are per tenant too: one unreachable database
            // costs its own row, not the screen.
            var (installed, error) = await _tenantModels.ReadAsync(tenant, ct);
            var offered = images.FirstOrDefault(i => string.Equals(i.Image, tenant.ImageTag, StringComparison.Ordinal));
            var installedRows = installed.Select(m =>
            {
                latestByName.TryGetValue(m.Name, out var latest);
                var offers = offered?.Models
                    .FirstOrDefault(o => string.Equals(o.Name, m.Name, StringComparison.OrdinalIgnoreCase))?.Version;
                var outdated = !m.IsSystem && ModelGraph.IsOutdated(m.Version, latest);
                return new
                {
                    m.Name,
                    m.Version,
                    m.IsSystem,
                    m.IsEnabled,
                    m.CompilationStatus,
                    m.CompilationError,
                    compiles = ModelGraph.CompilesOk(m.CompilationStatus),
                    offers,
                    latest,
                    outdated,
                };
            }).ToList();
            var missing = latestByName.Keys
                .Where(name =>
                {
                    graph.TryGetValue(name, out var node);
                    return node is not { IsSystem: true }
                           && installed.All(m => !string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                })
                .Select(name => new { name, version = latestByName[name] })
                .ToList();

            tenants.Add(new
            {
                id = tenant.Id,
                slug = tenant.Slug,
                imageTag = tenant.ImageTag,
                status = tenant.Status.ToString(),
                error,
                sourceImage,
                // Null, not empty: "this image declares nothing" and "we could not read
                // it" are different answers and the screen should not merge them.
                carries = offered?.Models.Select(m => new { name = m.Name, version = m.Version }).ToList(),
                outdatedCount = installedRows.Count(m => m.outdated),
                brokenCount = installedRows.Count(m => !m.compiles),
                missingCount = missing.Count,
                installed = installedRows,
                missing,
            });
        }

        return Ok(new
        {
            registry,
            sourceImage,
            models,
            images = images.Select(i => new
            {
                i.Image,
                i.Repository,
                i.Tag,
                platform = i.Platform,
                workspace = i.WorkspaceCommit,
                models = i.Models
                    .Where(m => !StandModel.IsShippedName(m.Name))
                    .Select(m => new { name = m.Name, version = m.Version }).ToList(),
            }),
            tenants,
        });
    }

    /// <summary>
    /// The distribution image the page installs FROM. Tenants usually run
    /// <c>zuloone-core</c>, which carries no tree — the newest <c>zuloone</c> tag does.
    /// </summary>
    public static string? PickDistribution(IReadOnlyList<CatalogueImage> images) =>
        images
            .Where(i => !i.Repository.EndsWith("zuloone-core", StringComparison.OrdinalIgnoreCase))
            .Where(i => i.Models.Count > 0)
            .OrderByDescending(i => i.Tag, Comparer<string>.Create(ModelGraph.CompareVersions))
            .Select(i => i.Image)
            .FirstOrDefault()
        ?? images
            .Where(i => i.Models.Count > 0)
            .OrderByDescending(i => i.Models.Count)
            .Select(i => i.Image)
            .FirstOrDefault();

    /// <summary>Which models to put into one running tenant, and where from.</summary>
    public sealed record InstallRequest(string? ImageTag, string[] Models);

    /// <summary>
    /// Installs models into a tenant that keeps serving. No container is recreated.
    /// </summary>
    /// <remarks>
    /// The counterpart to a rollout, and deliberately a different verb. A rollout moves
    /// tenants between images and can be undone by pinning the old one back; this puts
    /// metadata into a live tenant, where the pre-install snapshot is the only undo.
    /// </remarks>
    [HttpPost("/api/tenants/{id:guid}/install-models")]
    public async Task<IActionResult> Install(Guid id, [FromBody] InstallRequest request, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });
        if (string.IsNullOrWhiteSpace(tenant.DatabasePassword))
            return BadRequest(new { error = $"'{tenant.Slug}' has no stored database password, so it cannot be snapshotted — and an install with no way back is not one." });

        var wanted = request.Models ?? [];
        var source = string.IsNullOrWhiteSpace(request.ImageTag) ? tenant.ImageTag : request.ImageTag;
        if (wanted.Length > 0)
        {
            var graph = (await _trees.ReadGraphAsync(source, ct))
                .ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
            wanted = ModelGraph.Expand(wanted, graph)
                .Where(name => !StandModel.IsShippedName(name))
                .Where(name => !graph.TryGetValue(name, out var node) || !node.IsSystem)
                .ToArray();
        }

        var job = await _queue.EnqueueAsync(
            JobKind.InstallModels, tenant.Id, tenant.Slug,
            new InstallModelsPayload(request.ImageTag, wanted),
            OperatorIdentity.Of(User), ct);

        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary>
    /// Schema sync, entity types, then scripts — no tree, no snapshot.
    /// </summary>
    [HttpPost("/api/tenants/{id:guid}/compile-models")]
    public async Task<IActionResult> Compile(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });
        if (string.IsNullOrWhiteSpace(tenant.JwtSigningKey))
            return BadRequest(new { error = $"'{tenant.Slug}' has no signing key, so the panel cannot authenticate to it." });

        var job = await _queue.EnqueueAsync(
            JobKind.CompileModels, tenant.Id, tenant.Slug, payload: null,
            OperatorIdentity.Of(User), ct);

        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary>What a rollout should do, from the screen.</summary>
    public sealed record RolloutRequest(
        string? ImageTag,
        bool SetModels,
        string[]? Models,
        Guid[] TenantIds,
        bool StopOnFailure = true,
        bool StopOnCompileErrors = true);

    /// <summary>
    /// Starts a wave: several tenants moved one at a time, stopping on the first that
    /// does not come up.
    /// </summary>
    /// <remarks>
    /// Validated HERE, before anything is queued, so a mistake costs a 400 rather than
    /// a wave that dies on its first tenant with a snapshot already taken. Checking it
    /// inside the job would be too late: by then the operator has walked away.
    /// </remarks>
    [HttpPost("/api/rollouts")]
    public async Task<IActionResult> Rollout([FromBody] RolloutRequest request, CancellationToken ct)
    {
        if (request.TenantIds is null || request.TenantIds.Length == 0)
            return BadRequest(new { error = "Name at least one tenant to move." });
        if (string.IsNullOrWhiteSpace(request.ImageTag) && !request.SetModels)
            return BadRequest(new { error = "A rollout must change the image, the model set, or both." });

        var tenants = await _db.Tenants.AsNoTracking()
            .Where(t => request.TenantIds.Contains(t.Id))
            .ToListAsync(ct);
        var missing = request.TenantIds.Where(id => tenants.All(t => t.Id != id)).ToList();
        if (missing.Count > 0)
            return BadRequest(new { error = $"{missing.Count} of the named tenants are not in the registry." });

        // A model the target image does not carry would install nothing and say
        // nothing — the tenant would come up healthy and short of what was asked for.
        if (request.SetModels && request.Models is { Length: > 0 } && !request.Models.Contains("*"))
        {
            var (registry, _) = ImagesController.SplitImage(_fleet.DefaultImage);
            if (registry is not null)
            {
                var images = await _catalogue.ReadAsync(registry, ct);
                // Against the target image when one is named; otherwise against what
                // each tenant already runs, since that is what will install them.
                var targets = string.IsNullOrWhiteSpace(request.ImageTag)
                    ? tenants.Select(t => t.ImageTag).Distinct(StringComparer.Ordinal).ToList()
                    : [request.ImageTag!];

                foreach (var target in targets)
                {
                    var image = images.FirstOrDefault(i => string.Equals(i.Image, target, StringComparison.Ordinal));
                    if (image is null) continue;   // unreadable labels are not a veto
                    var absent = request.Models
                        .Where(m => !image.Models.Any(o => string.Equals(o.Name, m, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (absent.Count > 0)
                    {
                        return BadRequest(new
                        {
                            error = $"{target} does not carry {string.Join(", ", absent)}. "
                                  + "Installing a model an image lacks does nothing and reports nothing.",
                        });
                    }
                }
            }
        }

        // Ordered as the caller listed them: a wave is a sequence, and the operator
        // choosing which tenant goes first is the point.
        var ordered = request.TenantIds.ToArray();
        var models = request.SetModels && request.Models is not null
            ? System.Text.Json.JsonSerializer.Serialize(request.Models)
            : null;

        var job = await _queue.EnqueueAsync(
            JobKind.Rollout, null, null,
            new RolloutPayload(request.ImageTag, request.SetModels, models, ordered,
                request.StopOnFailure, request.StopOnCompileErrors),
            OperatorIdentity.Of(User), ct);

        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }
}

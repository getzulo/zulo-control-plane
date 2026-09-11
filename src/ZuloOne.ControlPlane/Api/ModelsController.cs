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
    private readonly FleetConfig _fleet;
    private readonly IJobQueue _queue;

    public ModelsController(
        ControlPlaneDbContext db,
        RegistryModelCatalog catalogue,
        TenantModelsService tenantModels,
        FleetConfig fleet,
        IJobQueue queue)
    {
        _db = db;
        _catalogue = catalogue;
        _tenantModels = tenantModels;
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

        // model → version → the tags that carry it. Sorted newest-looking first so the
        // row reads as "this is current, these are the older ones still available".
        var models = images
            .SelectMany(i => i.Models.Select(m => (i, m)))
            .GroupBy(x => x.m.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                model = g.Key,
                versions = g
                    .GroupBy(x => x.m.Version, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(v => v.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(v => new
                    {
                        version = v.Key,
                        images = v.Select(x => x.i.Image).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList(),
                    })
                    .ToList(),
            })
            .ToList();

        var tenants = new List<object>();
        foreach (var tenant in await _db.Tenants.AsNoTracking().OrderBy(t => t.Slug).ToListAsync(ct))
        {
            // Per tenant, and failures are per tenant too: one unreachable database
            // costs its own row, not the screen.
            var (installed, error) = await _tenantModels.ReadAsync(tenant, ct);
            var offered = images.FirstOrDefault(i => string.Equals(i.Image, tenant.ImageTag, StringComparison.Ordinal));

            tenants.Add(new
            {
                id = tenant.Id,
                slug = tenant.Slug,
                imageTag = tenant.ImageTag,
                status = tenant.Status.ToString(),
                error,
                // Null, not empty: "this image declares nothing" and "we could not read
                // it" are different answers and the screen should not merge them.
                carries = offered?.Models.Select(m => new { name = m.Name, version = m.Version }).ToList(),
                installed = installed.Select(m => new
                {
                    m.Name,
                    m.Version,
                    m.IsSystem,
                    m.IsEnabled,
                    m.CompilationStatus,
                    m.CompilationError,
                    offers = offered?.Models
                        .FirstOrDefault(o => string.Equals(o.Name, m.Name, StringComparison.OrdinalIgnoreCase))?.Version,
                }).ToList(),
            });
        }

        return Ok(new
        {
            registry,
            models,
            images = images.Select(i => new
            {
                i.Image,
                i.Repository,
                i.Tag,
                platform = i.Platform,
                workspace = i.WorkspaceCommit,
                models = i.Models.Select(m => new { name = m.Name, version = m.Version }).ToList(),
            }),
            tenants,
        });
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

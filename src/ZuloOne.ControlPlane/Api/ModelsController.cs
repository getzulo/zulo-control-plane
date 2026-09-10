using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public ModelsController(
        ControlPlaneDbContext db,
        RegistryModelCatalog catalogue,
        TenantModelsService tenantModels,
        FleetConfig fleet)
    {
        _db = db;
        _catalogue = catalogue;
        _tenantModels = tenantModels;
        _fleet = fleet;
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
}

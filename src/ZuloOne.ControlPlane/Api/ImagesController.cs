using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

public record UpgradeRequest(string ImageTag);

/// <summary>
/// What the fleet can run, and moving a tenant onto it.
/// </summary>
[ApiController]
[Route("api/images")]
[Produces("application/json")]
public class ImagesController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IJobQueue _queue;
    private readonly FleetSettings _fleet;

    public ImagesController(
        ControlPlaneDbContext db, IHttpClientFactory http, IJobQueue queue, IOptions<FleetSettings> fleet)
    {
        _db = db;
        _http = http;
        _queue = queue;
        _fleet = fleet.Value;
    }

    /// <summary>
    /// Tags in the registry, newest first, with who is running what.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // The repository is whatever the default image names, so the panel does not
        // need to be told the registry address twice.
        var (registry, repository) = SplitImage(_fleet.DefaultImage);
        if (registry is null)
            return Ok(new { registry = (string?)null, error = "Fleet:DefaultImage does not name a registry, so its tags cannot be listed.", tags = Array.Empty<object>() });

        List<string> tags;
        try
        {
            using var client = _http.CreateClient("registry");
            var json = await client.GetStringAsync($"http://{registry}/v2/{repository}/tags/list", ct);
            tags = JsonDocument.Parse(json).RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
                : [];
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Could not read the registry at {registry}: {ex.Message}" });
        }

        var inUse = await _db.Tenants.AsNoTracking()
            .GroupBy(t => t.ImageTag)
            .Select(g => new { Tag = g.Key, Slugs = g.Select(t => t.Slug).ToList() })
            .ToListAsync(ct);

        // Releases and CI builds are different things and must not be interleaved.
        // A release is CalVer with a non-zero month; `2026.0.<run>` is the sentinel
        // for "not a release", and sha-<commit> is a build artefact.
        var releases = tags.Where(IsRelease).OrderByDescending(t => t, CalVer).ToList();
        var builds = tags.Where(t => !IsRelease(t)).OrderByDescending(t => t, StringComparer.Ordinal).ToList();

        return Ok(new
        {
            registry,
            repository,
            defaultImage = _fleet.DefaultImage,
            releases = releases.Select(t => Describe(t, registry, repository, inUse)),
            builds = builds.Select(t => Describe(t, registry, repository, inUse)),
        });

        object Describe(string tag, string reg, string repo, IEnumerable<dynamic> used)
        {
            var full = $"{reg}/{repo}:{tag}";
            var slugs = used.FirstOrDefault(u => (string)u.Tag == full)?.Slugs ?? new List<string>();
            return new
            {
                tag,
                image = full,
                inUseBy = slugs,
                // The drift worth surfacing: a new tenant would start on the default
                // image, and if that is older than what the fleet already runs, the
                // newest customer gets the oldest code.
                isDefault = string.Equals(full, _fleet.DefaultImage, StringComparison.Ordinal),
            };
        }
    }

    /// <summary>
    /// Moves a tenant to another tag. Snapshots first — that snapshot is the only
    /// way back past a forward-only migration.
    /// </summary>
    [HttpPost("/api/tenants/{id:guid}/upgrade")]
    public async Task<IActionResult> Upgrade(Guid id, [FromBody] UpgradeRequest request, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });
        if (string.IsNullOrWhiteSpace(request.ImageTag))
            return BadRequest(new { error = "Name the image to move to." });
        if (string.Equals(tenant.ImageTag, request.ImageTag, StringComparison.Ordinal))
            return BadRequest(new { error = $"'{tenant.Slug}' is already on {request.ImageTag}." });
        if (string.IsNullOrWhiteSpace(tenant.DatabasePassword))
            // Without it there is no snapshot, and without a snapshot there is no
            // rollback — refuse rather than upgrade blind.
            return Conflict(new { error = $"The registry holds no database password for '{tenant.Slug}', so it cannot be snapshotted. Adopt it properly before upgrading." });

        var job = await _queue.EnqueueAsync(
            JobKind.Upgrade, tenant.Id, tenant.Slug, new UpgradePayload(request.ImageTag),
            User.Identity?.Name ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value, ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary><c>host:port/repo:tag</c> → registry and repository.</summary>
    private static (string? Registry, string Repository) SplitImage(string image)
    {
        var withoutTag = image.Contains(':') && image.LastIndexOf(':') > image.LastIndexOf('/')
            ? image[..image.LastIndexOf(':')]
            : image;
        var slash = withoutTag.IndexOf('/');
        // A registry host is recognisable by carrying a port or a dot; without one
        // this is a Docker Hub name and there is no local registry to query.
        if (slash < 0) return (null, withoutTag);
        var head = withoutTag[..slash];
        return head.Contains(':') || head.Contains('.') ? (head, withoutTag[(slash + 1)..]) : (null, withoutTag);
    }

    private static bool IsRelease(string tag)
    {
        var parts = tag.Split('.');
        return parts.Length == 3
            && int.TryParse(parts[0], out var year) && year > 2000
            && int.TryParse(parts[1], out var month) && month is >= 1 and <= 12
            && int.TryParse(parts[2], out _);
    }

    /// <summary>
    /// CalVer, NOT lexicographic. Ordinal string comparison puts 2026.9.10 below
    /// 2026.9.4, which is exactly backwards at the moment someone is choosing what
    /// to upgrade to.
    /// </summary>
    private static readonly IComparer<string> CalVer = Comparer<string>.Create((a, b) =>
    {
        var x = a.Split('.'); var y = b.Split('.');
        for (var i = 0; i < 3; i++)
        {
            var c = int.Parse(x[i]).CompareTo(int.Parse(y[i]));
            if (c != 0) return c;
        }
        return 0;
    });
}

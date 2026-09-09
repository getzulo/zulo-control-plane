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

        // Which tags point at the same image. The registry has no "delete a tag" —
        // only "delete a manifest by digest", and that takes every tag on it. In
        // this registry EVERY tag shares its digest with at least one other, so a
        // delete button that did not show this would be a trap: removing the
        // build-artefact tag sha-a6b7e08 also removes 2026.0.33, which the whole
        // fleet is pinned to.
        var digests = await ResolveDigestsAsync(registry, repository, tags, ct);

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
            var digest = digests.GetValueOrDefault(tag);
            var siblings = digest is null
                ? new List<string>()
                : digests.Where(kv => kv.Value == digest && kv.Key != tag).Select(kv => kv.Key).OrderBy(x => x).ToList();
            var blocker = digest is null ? "its digest could not be read" : WhyUndeletable(tag, siblings, reg, repo, used);
            return new
            {
                tag,
                image = full,
                inUseBy = slugs,
                // The drift worth surfacing: a new tenant would start on the default
                // image, and if that is older than what the fleet already runs, the
                // newest customer gets the oldest code.
                isDefault = string.Equals(full, _fleet.DefaultImage, StringComparison.Ordinal),
                digest,
                // Deleting this tag deletes these too — they are the same manifest.
                alsoTagged = siblings,
                canDelete = blocker is null,
                deleteBlockedBy = blocker,
            };
        }
    }

    /// <summary>
    /// Removes a manifest from the registry — and with it EVERY tag pointing at it.
    /// </summary>
    /// <remarks>
    /// There is no per-tag delete in the registry API. <c>DELETE /v2/{repo}/manifests/{digest}</c>
    /// is the only removal there is, so "delete this tag" always means "delete this
    /// image and all its names". Measured on this registry: every tag shares a
    /// digest with at least one other, and <c>sha-a6b7e08</c>, <c>main</c> and
    /// <c>2026.0.33</c> are one image — the one both tenants run.
    ///
    /// <para>
    /// So the guards examine the whole sibling set, not the tag that was clicked.
    /// If any name on the manifest is in use, is a release, or is the fleet default,
    /// the whole manifest stays.
    /// </para>
    ///
    /// <para>
    /// This frees no disk on its own. Blobs survive until <c>registry
    /// garbage-collect</c> runs on the registry host, which the panel cannot reach —
    /// it holds a socket proxy for the app host only.
    /// </para>
    /// </remarks>
    [HttpDelete("{tag}")]
    public async Task<IActionResult> Delete(string tag, [FromQuery] string? confirmTag, CancellationToken ct)
    {
        if (!string.Equals(confirmTag, tag, StringComparison.Ordinal))
            return BadRequest(new { error = $"Pass confirmTag={tag} to confirm." });

        var (registry, repository) = SplitImage(_fleet.DefaultImage);
        if (registry is null)
            return BadRequest(new { error = "Fleet:DefaultImage does not name a registry." });

        List<string> tags;
        using var client = _http.CreateClient("registry");
        try
        {
            var json = await client.GetStringAsync($"http://{registry}/v2/{repository}/tags/list", ct);
            tags = JsonDocument.Parse(json).RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
                : [];
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Could not read the registry: {ex.Message}" });
        }
        if (!tags.Contains(tag)) return NotFound(new { error = $"No tag '{tag}' in {repository}." });

        var digests = await ResolveDigestsAsync(registry, repository, tags, ct);
        if (!digests.TryGetValue(tag, out var digest) || digest is null)
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Could not read the digest of '{tag}', so there is nothing safe to delete." });

        var siblings = digests.Where(kv => kv.Value == digest && kv.Key != tag).Select(kv => kv.Key).OrderBy(x => x).ToList();
        var inUse = await _db.Tenants.AsNoTracking()
            .GroupBy(t => t.ImageTag)
            .Select(g => new { Tag = g.Key, Slugs = g.Select(x => x.Slug).ToList() })
            .ToListAsync(ct);

        var blocker = WhyUndeletable(tag, siblings, registry, repository, inUse);
        if (blocker is not null) return Conflict(new { error = blocker, digest, alsoTagged = siblings });

        var response = await client.DeleteAsync($"http://{registry}/v2/{repository}/manifests/{digest}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed
                    ? "The registry refuses deletes. Set REGISTRY_STORAGE_DELETE_ENABLED=true on it."
                    : $"The registry rejected the delete ({(int)response.StatusCode}): {body}",
            });
        }

        return Ok(new
        {
            success = true,
            digest,
            removed = siblings.Prepend(tag).OrderBy(x => x).ToList(),
            note = "The manifest is gone. Disk is freed only when `registry garbage-collect` runs on the registry host.",
        });
    }

    /// <summary>
    /// Why this manifest may not be removed, or null when it may. Judged over every
    /// tag on it, because they die together.
    /// </summary>
    private string? WhyUndeletable(string tag, List<string> siblings, string registry, string repository, IEnumerable<dynamic> inUse)
    {
        foreach (var name in siblings.Prepend(tag))
        {
            var full = $"{registry}/{repository}:{name}";

            var users = inUse.FirstOrDefault(u => (string)u.Tag == full)?.Slugs as List<string>;
            if (users is { Count: > 0 })
                return name == tag
                    ? $"'{tag}' is what {string.Join(", ", users)} runs."
                    : $"'{tag}' is the same image as '{name}', which {string.Join(", ", users)} runs.";

            if (string.Equals(full, _fleet.DefaultImage, StringComparison.Ordinal))
                return name == tag
                    ? $"'{tag}' is the fleet default — every new tenant starts on it."
                    : $"'{tag}' is the same image as '{name}', the fleet default.";

            // A release is what a tenant rolls BACK to when an upgrade goes wrong.
            // Keeping every one of them costs a manifest; losing one costs the only
            // way back.
            if (IsRelease(name))
                return name == tag
                    ? $"'{tag}' is a release. Releases are kept — a tenant may need to roll back onto it."
                    : $"'{tag}' is the same image as release '{name}', which is kept so a tenant can roll back onto it.";
        }
        return null;
    }

    /// <summary>
    /// Tag → manifest digest. One HEAD per tag; the registry is on the LAN and the
    /// tag count is in the tens, so this costs less than caching it would.
    /// </summary>
    private async Task<Dictionary<string, string?>> ResolveDigestsAsync(
        string registry, string repository, List<string> tags, CancellationToken ct)
    {
        // Without these the registry answers with the v1 schema, whose digest is NOT
        // the one DELETE accepts — the delete then 404s on a manifest that plainly
        // exists.
        const string Accept = "application/vnd.docker.distribution.manifest.v2+json,"
                            + "application/vnd.oci.image.manifest.v1+json,"
                            + "application/vnd.docker.distribution.manifest.list.v2+json,"
                            + "application/vnd.oci.image.index.v1+json";

        using var client = _http.CreateClient("registry");
        var result = new Dictionary<string, string?>();
        foreach (var tag in tags)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, $"http://{registry}/v2/{repository}/manifests/{tag}");
                request.Headers.TryAddWithoutValidation("Accept", Accept);
                using var response = await client.SendAsync(request, ct);
                result[tag] = response.Headers.TryGetValues("Docker-Content-Digest", out var v) ? v.FirstOrDefault() : null;
            }
            catch
            {
                // One unreadable tag must not blank the whole screen; it simply
                // cannot be deleted, and Describe says so.
                result[tag] = null;
            }
        }
        return result;
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

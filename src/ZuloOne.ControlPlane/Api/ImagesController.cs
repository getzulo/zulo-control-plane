using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

using ZuloOne.ControlPlane.Auth;

namespace ZuloOne.ControlPlane.Api;

public record UpgradeRequest(string ImageTag);

public record PromoteRequest(string? Version, string? Notes);

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
    private readonly FleetConfig _fleet;

    public ImagesController(
        ControlPlaneDbContext db, IHttpClientFactory http, IJobQueue queue, FleetConfig fleet)
    {
        _db = db;
        _http = http;
        _queue = queue;
        _fleet = fleet;
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
        var builds = tags
            .Where(t => !IsRelease(t) && !IsRedundantCommitTag(t, digests))
            .OrderByDescending(t => t, StringComparer.Ordinal)
            .ToList();

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
            OperatorIdentity.Of(User), ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary>
    /// Turns a CI build into a release: the same manifest, under a CalVer tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole release procedure. Until now it was <c>git tag vYYYY.M.P
    /// &amp;&amp; git push --tags</c> and a wait for CI to rebuild — which meant
    /// leaving the panel, and meant the released image was BUILT AGAIN rather than
    /// being the artefact that had just been tested.
    /// </para>
    ///
    /// <para>
    /// Promotion copies no bytes. It reads the manifest by its build tag and PUTs
    /// the identical document under the release tag, so both names resolve to one
    /// digest — verified on this registry: source and promoted digests came back
    /// equal, and the PUT answered 201.
    /// </para>
    ///
    /// <para>
    /// The consequence is worth stating where an operator will read it: the two
    /// tags are one image and cannot be separated. Deleting either removes it from
    /// under both. <see cref="Delete"/> already refuses on exactly that basis.
    /// </para>
    /// </remarks>
    [HttpPost("{tag}/promote")]
    public async Task<IActionResult> Promote(string tag, [FromBody] PromoteRequest? request, CancellationToken ct)
    {
        var (registry, repository) = SplitImage(_fleet.DefaultImage);
        if (registry is null)
            return BadRequest(new { error = "Fleet:DefaultImage does not name a registry." });

        if (IsRelease(tag))
            return BadRequest(new { error = $"'{tag}' is already a release. A release is promoted from a build." });

        using var client = _http.CreateClient("registry");

        // Accept matters: without these the registry answers with the v1 schema,
        // whose digest is not the one the fleet would pull.
        const string Accept = "application/vnd.docker.distribution.manifest.v2+json,"
                            + "application/vnd.oci.image.manifest.v1+json,"
                            + "application/vnd.docker.distribution.manifest.list.v2+json,"
                            + "application/vnd.oci.image.index.v1+json";

        HttpResponseMessage source;
        try
        {
            using var read = new HttpRequestMessage(HttpMethod.Get, $"http://{registry}/v2/{repository}/manifests/{tag}");
            read.Headers.TryAddWithoutValidation("Accept", Accept);
            source = await client.SendAsync(read, ct);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Could not read the registry: {ex.Message}" });
        }

        if (source.StatusCode == System.Net.HttpStatusCode.NotFound)
            return NotFound(new { error = $"'{tag}' is not in {repository}." });
        if (!source.IsSuccessStatusCode)
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"The registry answered {(int)source.StatusCode} for '{tag}'." });

        var body = await source.Content.ReadAsByteArrayAsync(ct);
        var mediaType = source.Content.Headers.ContentType?.ToString();
        var sourceDigest = source.Headers.TryGetValues("Docker-Content-Digest", out var d) ? d.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(sourceDigest))
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"The registry did not return a manifest for '{tag}'." });

        var version = string.IsNullOrWhiteSpace(request?.Version)
            ? await NextVersionAsync(registry, repository, ct)
            : request!.Version!.Trim();

        if (!IsRelease(version))
            return BadRequest(new { error = $"'{version}' is not a release version. Required shape: YYYY.M.P, month 1-12." });

        // Immutable, and this is the check that makes them so. A release tag that
        // can be moved is a version number that means nothing — a tenant pinned to
        // it would silently change what it runs.
        var existing = await _db.Releases.FirstOrDefaultAsync(r => r.Version == version && r.Repository == repository, ct);
        if (existing is not null)
            return Conflict(new
            {
                error = $"{version} was already released on {existing.PromotedAt:yyyy-MM-dd} from {existing.SourceTag}. "
                      + "Releases are immutable — promote to a new patch instead.",
            });

        using var head = new HttpRequestMessage(HttpMethod.Head, $"http://{registry}/v2/{repository}/manifests/{version}");
        head.Headers.TryAddWithoutValidation("Accept", Accept);
        using var occupied = await client.SendAsync(head, ct);
        if (occupied.IsSuccessStatusCode)
            return Conflict(new { error = $"The registry already holds {repository}:{version}, with no release recorded for it." });

        using var write = new HttpRequestMessage(HttpMethod.Put, $"http://{registry}/v2/{repository}/manifests/{version}")
        {
            Content = new ByteArrayContent(body),
        };
        write.Content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        using var written = await client.SendAsync(write, ct);
        if (!written.IsSuccessStatusCode)
        {
            var detail = await written.Content.ReadAsStringAsync(ct);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"The registry rejected the release tag ({(int)written.StatusCode}): {detail}" });
        }

        var release = new Release
        {
            Version = version,
            SourceTag = tag,
            Digest = sourceDigest,
            Repository = repository,
            Notes = string.IsNullOrWhiteSpace(request?.Notes) ? null : request!.Notes!.Trim(),
            PromotedBy = OperatorIdentity.Of(User),
            PromotedAt = DateTime.UtcNow,
        };
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            success = true,
            version,
            image = $"{registry}/{repository}:{version}",
            digest = sourceDigest,
            promotedFrom = tag,
            note = "Same manifest, second name — the bytes that were tested are the bytes that ship.",
        });
    }

    /// <summary>Releases, newest first.</summary>
    [HttpGet("/api/releases")]
    public async Task<IActionResult> Releases(CancellationToken ct)
    {
        var (_, repository) = SplitImage(_fleet.DefaultImage);
        var rows = await _db.Releases.AsNoTracking()
            .Where(r => r.Repository == repository)
            .ToListAsync(ct);
        return Ok(rows.OrderByDescending(r => r.Version, CalVer));
    }

    /// <summary>
    /// The next patch in the current month — 2026.9.4 becomes 2026.9.5, and the
    /// first release of a new month starts at .0.
    /// </summary>
    /// <remarks>
    /// Derived from the REGISTRY rather than from the release table, because the
    /// registry is what a collision would actually be with. A tag can exist
    /// without a release row (anything cut before this endpoint existed).
    /// </remarks>
    private async Task<string> NextVersionAsync(string registry, string repository, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var prefix = $"{now.Year}.{now.Month}.";

        List<string> tags = [];
        try
        {
            using var client = _http.CreateClient("registry");
            var json = await client.GetStringAsync($"http://{registry}/v2/{repository}/tags/list", ct);
            if (JsonDocument.Parse(json).RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                tags = t.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList();
        }
        catch
        {
            // Unreadable registry is reported by the caller's own request; here it
            // only means the suggestion starts from zero.
        }

        var highest = tags
            .Where(x => x.StartsWith(prefix, StringComparison.Ordinal))
            .Select(x => int.TryParse(x[prefix.Length..], out var p) ? p : -1)
            .DefaultIfEmpty(-1)
            .Max();

        return $"{prefix}{highest + 1}";
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
    /// A <c>sha-&lt;commit&gt;</c> tag whose manifest already carries a readable name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CI puts two names on every build — the commit and the build number — and both
    /// are tags on ONE manifest. Listing tags verbatim therefore showed each image
    /// twice, once as <c>2026.0.45</c> and once as <c>sha-67eee43</c>, each row
    /// naming the other in its "also tagged" column. Half the screen was the same
    /// images read backwards.
    /// </para>
    ///
    /// <para>
    /// Hidden only when something else covers it. A commit tag whose digest could
    /// not be read, or whose manifest has no other name, stays on the list: an image
    /// nobody can see is an image nobody can delete, and this screen is the only
    /// place a manifest can be removed at all.
    /// </para>
    ///
    /// <para>
    /// The commit does not disappear — it is still on the build's own row under
    /// <c>alsoTagged</c>, which is where a reader looks for it anyway.
    /// </para>
    /// </remarks>
    private static bool IsRedundantCommitTag(string tag, Dictionary<string, string?> digests)
    {
        if (!tag.StartsWith(CommitTagPrefix, StringComparison.Ordinal)) return false;
        var digest = digests.GetValueOrDefault(tag);
        if (digest is null) return false;
        return digests.Any(kv => kv.Value == digest
                              && !kv.Key.StartsWith(CommitTagPrefix, StringComparison.Ordinal));
    }

    /// <summary>What CI names an image after the commit that produced it.</summary>
    private const string CommitTagPrefix = "sha-";

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

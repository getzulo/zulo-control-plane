using System.Text.Json;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>One image in the registry and the model set its labels declare.</summary>
public sealed record CatalogueImage(
    string Repository,
    string Tag,
    string Image,
    string? Digest,
    string? Platform,
    string? WorkspaceCommit,
    IReadOnlyList<CatalogueModel> Models);

/// <summary>One model an image carries, at the version it carries.</summary>
public sealed record CatalogueModel(string Name, string Version);

/// <summary>
/// What the registry holds, read from the registry — not from the Docker daemon.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TenantContainerService.LabelsAsync"/> asks the daemon on the app host,
/// which is the right trade for an image that is already running there and the wrong
/// one here: an operator building a catalogue is looking at images nobody has pulled
/// yet, and the daemon reports nothing for those. A catalogue that goes blank exactly
/// where the interesting images are is worse than none.
/// </para>
///
/// <para>
/// Cost is bounded by deduplicating on the manifest digest before fetching anything
/// heavy. CI puts at least two tags on every build — the commit and the build number
/// — so tags outnumber distinct images roughly two to one, and measured on this
/// registry 37 tags collapse to 19 digests. Only those are opened.
/// </para>
///
/// <para>
/// Repositories come from <c>/v2/_catalog</c> rather than a setting. The fleet's
/// default image names <c>zuloone-core</c>, the platform-only repository, while model
/// trees ship in <c>zuloone</c> — a catalogue keyed off the default would look in the
/// one place that never carries a model.
/// </para>
/// </remarks>
public sealed class RegistryModelCatalog
{
    /// <summary>Set by CI on a distribution image: "Name=Version,Name=Version".</summary>
    public const string ModelsLabel = "one.zulo.models";
    private const string PlatformLabel = "one.zulo.platform";
    private const string WorkspaceLabel = "one.zulo.workspace";

    // Without these the registry answers in the v1 schema, which carries no config
    // descriptor at all — the label read then finds nothing on an image that plainly
    // has labels.
    private const string ManifestAccept =
        "application/vnd.docker.distribution.manifest.v2+json,"
        + "application/vnd.oci.image.manifest.v1+json,"
        + "application/vnd.docker.distribution.manifest.list.v2+json,"
        + "application/vnd.oci.image.index.v1+json";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<RegistryModelCatalog> _logger;

    public RegistryModelCatalog(IHttpClientFactory http, ILogger<RegistryModelCatalog> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Every tag in the registry that declares a model set, with what it declares.
    /// Images without the label are not errors — they are the platform, and simply
    /// carry no business layer.
    /// </summary>
    public async Task<IReadOnlyList<CatalogueImage>> ReadAsync(string registry, CancellationToken ct = default)
    {
        using var client = _http.CreateClient("registry");
        var result = new List<CatalogueImage>();

        List<string> repositories;
        try
        {
            var json = await client.GetStringAsync($"http://{registry}/v2/_catalog", ct);
            repositories = JsonDocument.Parse(json).RootElement.TryGetProperty("repositories", out var r)
                && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the repositories of {Registry}.", registry);
            return result;
        }

        // Labels are a property of the MANIFEST, so two tags on one digest share them.
        var labelsByDigest = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var repository in repositories)
        {
            List<string> tags;
            try
            {
                var json = await client.GetStringAsync($"http://{registry}/v2/{repository}/tags/list", ct);
                tags = JsonDocument.Parse(json).RootElement.TryGetProperty("tags", out var t)
                    && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : [];
            }
            catch (Exception ex)
            {
                // One unreadable repository costs its own rows, not the screen.
                _logger.LogWarning(ex, "Could not list the tags of {Repository}.", repository);
                continue;
            }

            foreach (var tag in tags)
            {
                var digest = await DigestAsync(client, registry, repository, tag, ct);
                IReadOnlyDictionary<string, string> labels;
                if (digest is null)
                {
                    labels = new Dictionary<string, string>();
                }
                else if (!labelsByDigest.TryGetValue(digest, out var cached))
                {
                    labels = labelsByDigest[digest] = await LabelsAsync(client, registry, repository, digest, ct);
                }
                else
                {
                    labels = cached;
                }

                var models = ParseModels(labels.GetValueOrDefault(ModelsLabel));
                if (models.Count == 0) continue;

                result.Add(new CatalogueImage(
                    repository,
                    tag,
                    $"{registry}/{repository}:{tag}",
                    digest,
                    labels.GetValueOrDefault(PlatformLabel),
                    labels.GetValueOrDefault(WorkspaceLabel),
                    models));
            }
        }

        return result;
    }

    /// <summary>"Sales=1.2.0,Common=1.0.0" → the pairs, ignoring anything malformed.</summary>
    public static List<CatalogueModel> ParseModels(string? label) =>
        (label ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0)
            .Select(parts => new CatalogueModel(parts[0], parts[1]))
            .ToList();

    private async Task<string?> DigestAsync(
        HttpClient client, string registry, string repository, string tag, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, $"http://{registry}/v2/{repository}/manifests/{tag}");
            request.Headers.TryAddWithoutValidation("Accept", ManifestAccept);
            using var response = await client.SendAsync(request, ct);
            return response.Headers.TryGetValues("Docker-Content-Digest", out var v) ? v.FirstOrDefault() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Manifest → config descriptor → config blob → its labels. An index is followed
    /// to its first platform manifest; this registry is single-arch, but a multi-arch
    /// push would otherwise silently produce an image with no labels.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LabelsAsync(
        HttpClient client, string registry, string repository, string digest, CancellationToken ct)
    {
        var empty = new Dictionary<string, string>();
        try
        {
            var manifest = await GetJsonAsync(client, registry, repository, digest, ct);
            if (manifest is null) return empty;

            if (manifest.RootElement.TryGetProperty("manifests", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                // An index, and NOT necessarily one entry. buildkit publishes the
                // image alongside an attestation manifest whose platform is
                // "unknown/unknown" and which has no labels at all — measured here:
                // every distribution image is an OCI index of exactly those two.
                // Taking the first entry works only for as long as the real one
                // happens to be first.
                var inner = list.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Object)
                    .Where(e => !e.TryGetProperty("platform", out var p)
                             || !p.TryGetProperty("os", out var os)
                             || !string.Equals(os.GetString(), "unknown", StringComparison.Ordinal))
                    .Select(e => e.TryGetProperty("digest", out var d) ? d.GetString() : null)
                    .FirstOrDefault(d => d is not null);
                if (inner is null) return empty;
                manifest.Dispose();
                manifest = await GetJsonAsync(client, registry, repository, inner, ct);
                if (manifest is null) return empty;
            }

            var configDigest = manifest.RootElement.TryGetProperty("config", out var cfg)
                && cfg.TryGetProperty("digest", out var cd) ? cd.GetString() : null;
            manifest.Dispose();
            if (configDigest is null) return empty;

            var blob = await client.GetStringAsync($"http://{registry}/v2/{repository}/blobs/{configDigest}", ct);
            using var config = JsonDocument.Parse(blob);
            if (!config.RootElement.TryGetProperty("config", out var inner2)
                || !inner2.TryGetProperty("Labels", out var labels)
                || labels.ValueKind != JsonValueKind.Object)
            {
                return empty;
            }

            return labels.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the labels of {Repository}@{Digest}.", repository, digest);
            return empty;
        }
    }

    private static async Task<JsonDocument?> GetJsonAsync(
        HttpClient client, string registry, string repository, string reference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{registry}/v2/{repository}/manifests/{reference}");
        request.Headers.TryAddWithoutValidation("Accept", ManifestAccept);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

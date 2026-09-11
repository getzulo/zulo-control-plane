using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Pulls the model tree out of an image in the registry, without running it.
/// </summary>
/// <remarks>
/// <para>
/// A distribution image is the platform plus <c>COPY dist-workspace/ /opt/zuloone/workspace/</c>,
/// so the tree is a handful of files in one layer. Reading it here is what lets the
/// panel install models into a RUNNING tenant: the image stops being the delivery
/// vehicle and becomes just the place the content is kept.
/// </para>
///
/// <para>
/// Layers are walked newest-first and the walk stops at the first that carries the
/// tree. That order is not an optimisation detail — it is what keeps the cost at one
/// small blob instead of the platform's hundreds of megabytes, because CI's COPY is
/// the topmost layer by construction.
/// </para>
///
/// <para>
/// Returns a tar.gz with the <c>opt/zuloone/workspace/</c> prefix stripped, so what
/// comes out is exactly the shape the tenant's install endpoint expects and exactly
/// the shape CI staged going in.
/// </para>
/// </remarks>
public sealed class ImageTreeReader
{
    /// <summary>Where CI puts the tree inside a distribution image.</summary>
    private const string TreeRoot = "opt/zuloone/workspace/";

    /// <summary>
    /// Layers above this are not opened. A model tree is single-digit megabytes; the
    /// platform's are hundreds. Without the guard, an image whose top layer is NOT the
    /// tree would have the panel download the whole runtime to discover that.
    /// </summary>
    private const long MaxLayerBytes = 100L * 1024 * 1024;

    private const string ManifestAccept =
        "application/vnd.docker.distribution.manifest.v2+json,"
        + "application/vnd.oci.image.manifest.v1+json,"
        + "application/vnd.docker.distribution.manifest.list.v2+json,"
        + "application/vnd.oci.image.index.v1+json";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<ImageTreeReader> _logger;

    public ImageTreeReader(IHttpClientFactory http, ILogger<ImageTreeReader> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// The image's model tree as a tar.gz, or null when it carries none — which is
    /// the honest answer for a platform-only image rather than an error.
    /// </summary>
    /// <param name="models">
    /// When given, only these top-level model directories are packed. Sending the
    /// whole tree and letting the tenant sort it out would install models nobody
    /// asked for — the archive IS the selection on the receiving side.
    /// </param>
    public async Task<byte[]?> ReadTreeAsync(
        string image, IReadOnlyCollection<string>? models = null, CancellationToken ct = default)
    {
        var (registry, repository) = Api.ImagesController.SplitImage(image);
        if (registry is null) return null;
        var reference = image.Contains(':') && image.LastIndexOf(':') > image.LastIndexOf('/')
            ? image[(image.LastIndexOf(':') + 1)..]
            : "latest";

        using var client = _http.CreateClient("registry");
        var layers = await LayersAsync(client, registry, repository, reference, ct);
        if (layers.Count == 0) return null;

        // Newest first: CI's COPY is the topmost layer.
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var (digest, size) = layers[i];
            if (size > MaxLayerBytes)
            {
                _logger.LogDebug("Skipping layer {Digest} of {Size} MB — larger than a model tree can be.",
                    digest[..19], size / 1024 / 1024);
                continue;
            }

            var packed = await RepackAsync(client, registry, repository, digest, models, ct);
            if (packed is not null)
            {
                _logger.LogInformation("Model tree read from {Image}, layer {Digest}: {Size} KB packed.",
                    image, digest[..19], packed.Length / 1024);
                return packed;
            }
        }

        return null;
    }

    private async Task<List<(string Digest, long Size)>> LayersAsync(
        HttpClient client, string registry, string repository, string reference, CancellationToken ct)
    {
        var empty = new List<(string, long)>();
        try
        {
            using var manifest = await GetManifestAsync(client, registry, repository, reference, ct);
            if (manifest is null) return empty;

            var root = manifest.RootElement;
            if (root.TryGetProperty("manifests", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                // An index. buildkit publishes an attestation manifest beside the image
                // with platform "unknown/unknown" and no layers worth reading.
                var inner = list.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Object)
                    .Where(e => !e.TryGetProperty("platform", out var p)
                             || !p.TryGetProperty("os", out var os)
                             || !string.Equals(os.GetString(), "unknown", StringComparison.Ordinal))
                    .Select(e => e.TryGetProperty("digest", out var d) ? d.GetString() : null)
                    .FirstOrDefault(d => d is not null);
                if (inner is null) return empty;

                using var real = await GetManifestAsync(client, registry, repository, inner, ct);
                if (real is null) return empty;
                return ReadLayers(real.RootElement);
            }

            return ReadLayers(root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the manifest of {Repository}:{Reference}.", repository, reference);
            return empty;
        }

        static List<(string Digest, long Size)> ReadLayers(JsonElement manifest)
        {
            var result = new List<(string, long)>();
            if (!manifest.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var layer in layers.EnumerateArray())
            {
                var digest = layer.TryGetProperty("digest", out var d) ? d.GetString() : null;
                var size = layer.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                if (digest is not null) result.Add((digest, size));
            }
            return result;
        }
    }

    /// <summary>
    /// Reads one layer and, if it holds the tree, returns just that subtree re-packed
    /// with the prefix stripped. Null when the layer carries nothing from the tree.
    /// </summary>
    private async Task<byte[]?> RepackAsync(
        HttpClient client, string registry, string repository, string digest,
        IReadOnlyCollection<string>? models, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(
                $"http://{registry}/v2/{repository}/blobs/{digest}", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var blob = await response.Content.ReadAsStreamAsync(ct);
            await using var gzip = new GZipStream(blob, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);

            var output = new MemoryStream();
            var wrote = false;
            // The writer is disposed before the bytes are taken: gzip only flushes its
            // trailer on close, and an archive without it is unreadable by tar.
            await using (var outGzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            using (var writer = new TarWriter(outGzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                while (await reader.GetNextEntryAsync(copyData: true, ct) is { } entry)
                {
                    var name = entry.Name.TrimStart('.', '/');
                    if (!name.StartsWith(TreeRoot, StringComparison.Ordinal)) continue;
                    var relative = name[TreeRoot.Length..];
                    if (relative.Length == 0) continue;

                    if (models is { Count: > 0 })
                    {
                        var top = relative.Split('/', 2)[0];
                        if (!models.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
                    }

                    // Whiteouts mark deletions in an overlay and are not content.
                    if (Path.GetFileName(relative).StartsWith(".wh.", StringComparison.Ordinal)) continue;

                    var copy = new PaxTarEntry(entry.EntryType, relative);
                    if (entry.DataStream is not null)
                    {
                        var buffer = new MemoryStream();
                        await entry.DataStream.CopyToAsync(buffer, ct);
                        buffer.Position = 0;
                        copy.DataStream = buffer;
                    }
                    await writer.WriteEntryAsync(copy, ct);
                    wrote = true;
                }
            }

            return wrote ? output.ToArray() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read layer {Digest} of {Repository}.", digest, repository);
            return null;
        }
    }

    private static async Task<JsonDocument?> GetManifestAsync(
        HttpClient client, string registry, string repository, string reference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{registry}/v2/{repository}/manifests/{reference}");
        request.Headers.TryAddWithoutValidation("Accept", ManifestAccept);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

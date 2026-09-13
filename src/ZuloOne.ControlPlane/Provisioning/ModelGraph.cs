using System.Text.Json;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>One model as its <c>model.json</c> declares it, with names already resolved.</summary>
public sealed record ModelGraphNode(
    string Name,
    string Version,
    bool IsSystem,
    IReadOnlyList<string> DependsOn);

/// <summary>
/// Dependency walk and version compare for the models page. The graph comes from
/// <c>model.json</c> files inside a distribution image — the label
/// <c>one.zulo.models</c> only has names and versions.
/// </summary>
public static class ModelGraph
{
    /// <summary>
    /// Selected names plus every dependency, closest first so an install archive
    /// lists foundations before the thing that needs them. Unknown names are kept
    /// as selected — the image may still carry the folder.
    /// </summary>
    public static List<string> Expand(
        IEnumerable<string> selected,
        IReadOnlyDictionary<string, ModelGraphNode> graph)
    {
        var wanted = selected
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var seen = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(wanted);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!graph.TryGetValue(name, out var node)) continue;
            foreach (var dep in node.DependsOn)
            {
                if (seen.Add(dep)) queue.Enqueue(dep);
            }
        }

        return seen
            .OrderBy(n => graph.TryGetValue(n, out var node) && node.IsSystem ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads every top-level <c>model.json</c> from a workspace tree.
    /// Dependencies are resolved by <c>metaId</c>, then by the
    /// <c>Name-&gt;DependsOn</c> convention the files already carry.
    /// </summary>
    public static List<ModelGraphNode> Parse(IEnumerable<(string Path, string Json)> files)
    {
        var raw = new List<(string Name, string Version, bool IsSystem, string MetaId, List<string> DepIds, List<string> DepNames)>();
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, json) in files)
        {
            if (!IsTopLevelModelJson(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("object", out var obj)) continue;
                var name = obj.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var version = obj.TryGetProperty("modelVersion", out var v) ? v.GetString() ?? "" : "";
                var isSystem = obj.TryGetProperty("isSystem", out var sys) && sys.ValueKind == JsonValueKind.True;
                var metaId = obj.TryGetProperty("metaId", out var id) ? id.GetString() ?? "" : "";
                if (metaId.Length > 0) byId[metaId] = name;

                var depIds = new List<string>();
                var depNames = new List<string>();
                if (doc.RootElement.TryGetProperty("dependencies", out var deps)
                    && deps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dep in deps.EnumerateArray())
                    {
                        if (dep.TryGetProperty("dependsOnModelMetaId", out var did))
                        {
                            var value = did.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) depIds.Add(value);
                        }
                        if (dep.TryGetProperty("name", out var dn))
                        {
                            var label = dn.GetString() ?? "";
                            var arrow = label.LastIndexOf("->", StringComparison.Ordinal);
                            if (arrow >= 0 && arrow + 2 < label.Length)
                                depNames.Add(label[(arrow + 2)..]);
                        }
                    }
                }

                raw.Add((name, version, isSystem, metaId, depIds, depNames));
            }
            catch (JsonException)
            {
                // One broken file must not blank the graph.
            }
        }

        return raw.Select(r =>
        {
            var depends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in r.DepIds)
            {
                if (byId.TryGetValue(id, out var name)) depends.Add(name);
            }
            foreach (var name in r.DepNames) depends.Add(name);
            depends.Remove(r.Name);
            return new ModelGraphNode(r.Name, r.Version, r.IsSystem, depends.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
        }).ToList();
    }

    public static int CompareVersions(string? left, string? right)
    {
        var a = ParseVersion(left);
        var b = ParseVersion(right);
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    public static bool IsOutdated(string? installed, string? latest) =>
        !string.IsNullOrWhiteSpace(latest)
        && CompareVersions(installed, latest) < 0;

    public static bool CompilesOk(string? status) =>
        string.IsNullOrWhiteSpace(status)
        || status.Equals("Ok", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Success", StringComparison.OrdinalIgnoreCase);

    private static bool IsTopLevelModelJson(string path)
    {
        var name = path.Replace('\\', '/').TrimStart('.');
        name = name.TrimStart('/');
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
               && parts[1].Equals("model.json", StringComparison.OrdinalIgnoreCase);
    }

    private static int[] ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [0];
        return value.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var n) ? n : 0;
            })
            .DefaultIfEmpty(0)
            .ToArray();
    }
}

namespace ZuloOne.ControlPlane.Infra;

/// <summary>
/// The kinds of machine the fleet is made of. Patroni only knows Postgres;
/// everything else has to name itself.
/// </summary>
public static class InfraRoles
{
    public const string Postgres = "postgres";
    public const string Mongo = "mongo";
    public const string App = "app";
    public const string Etcd = "etcd";
    public const string Panel = "panel";
    public const string Ci = "ci";
    public const string Host = "host";

    public static readonly string[] DisplayOrder =
        [Postgres, Etcd, Mongo, App, Panel, Ci, Host];

    public static bool IsKnown(string? role) =>
        !string.IsNullOrWhiteSpace(role) && DisplayOrder.Contains(Normalize(role));

    public static string Normalize(string? role)
    {
        var r = (role ?? string.Empty).Trim().ToLowerInvariant();
        return r switch
        {
            "pg" or "postgres" or "postgresql" or "patroni" => Postgres,
            "mongo" or "mongodb" => Mongo,
            "app" or "apps" or "edge" => App,
            "etcd" or "witness" => Etcd,
            "panel" or "cp" or "controlplane" or "control-plane" => Panel,
            "ci" or "registry" => Ci,
            "" => string.Empty,
            _ => r,
        };
    }

    /// <summary>When a report or expected row omitted the role.</summary>
    public static string Infer(string node)
    {
        var n = node.Trim().ToLowerInvariant();
        if (n.Contains("pgw") || n.Contains("etcd") || n.Contains("witness")) return Etcd;
        if (n.Contains("mongo")) return Mongo;
        if (n.Contains("app")) return App;
        if (n.Contains("cp") || n.Contains("panel")) return Panel;
        if (n.Contains("ci") || n.Contains("registry")) return Ci;
        if (n.Contains("pg")) return Postgres;
        return Host;
    }

    /// <summary>
    /// <c>name:role</c> entries from <c>Infra:ExpectedNodes</c>. A bare name
    /// is accepted and the role is inferred, so a typo in the suffix is not
    /// required to make the row appear.
    /// </summary>
    public static IReadOnlyList<(string Name, string Role)> ParseExpected(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string Name, string Role)>();
        foreach (var raw in items)
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;
            string name, role;
            var split = item.LastIndexOf(':');
            if (split > 0)
            {
                name = item[..split].Trim();
                role = Normalize(item[(split + 1)..]);
            }
            else
            {
                name = item;
                role = Infer(item);
            }
            if (name.Length == 0 || !seen.Add(name)) continue;
            if (role.Length == 0) role = Infer(name);
            list.Add((name, role));
        }
        return list;
    }
}

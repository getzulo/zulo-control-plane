namespace ZuloOne.ControlPlane.Infra;

/// <summary>
/// An ad-hoc Docker prune the panel asked a node to run on its next health check.
/// </summary>
/// <remarks>
/// Same channel as cluster backups: the node already POSTs every five minutes,
/// and only a script that sends <c>X-Node-Commands: 1</c> is given the command.
/// In-memory on purpose — a restart drops it; the operator clicks again.
/// </remarks>
public sealed class NodePruneGate
{
    public static readonly TimeSpan ExpireAfter = TimeSpan.FromMinutes(30);

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPending(string node)
    {
        lock (_lock)
        {
            ExpireUnlocked();
            return _pending.ContainsKey(node);
        }
    }

    public IReadOnlyCollection<string> PendingNames()
    {
        lock (_lock)
        {
            ExpireUnlocked();
            return _pending.Keys.ToArray();
        }
    }

    public bool TryRequest(string node)
    {
        if (string.IsNullOrWhiteSpace(node)) return false;
        lock (_lock)
        {
            ExpireUnlocked();
            if (_pending.ContainsKey(node)) return false;
            _pending[node.Trim()] = DateTime.UtcNow;
            return true;
        }
    }

    public bool Claim(string node)
    {
        lock (_lock)
        {
            ExpireUnlocked();
            return _pending.Remove(node);
        }
    }

    private void ExpireUnlocked()
    {
        var cutoff = DateTime.UtcNow - ExpireAfter;
        foreach (var stale in _pending.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            _pending.Remove(stale);
    }
}

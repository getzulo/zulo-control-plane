namespace ZuloOne.ControlPlane.Infra;

/// <summary>
/// An ad-hoc cluster backup the panel asked for and the repository node has not
/// yet picked up.
/// </summary>
/// <remarks>
/// Lives in memory on purpose. A restart drops it; the operator clicks again.
/// Persisting this would need a table for a request that is meaningful for minutes
/// and then gone — and the job row already records that somebody asked.
/// </remarks>
public sealed class ClusterBackupRequest
{
    public required string Type { get; init; }
    public DateTime RequestedAt { get; init; }
    public string? RequestedBy { get; init; }
    public string? TargetNode { get; init; }
}

/// <summary>
/// The one in-flight "run pgBackRest now" the panel can hand to a node.
/// </summary>
/// <remarks>
/// The control plane cannot SSH to the repository host. The node already POSTs
/// its health every five minutes; the reply is the channel. Only a script that
/// sends <c>X-Node-Commands: 1</c> is given the command — older copies discard
/// the body, and handing it to them would consume the request into the void.
/// </remarks>
public sealed class ClusterBackupGate
{
    public static readonly TimeSpan ExpireAfter = TimeSpan.FromMinutes(30);

    private readonly object _lock = new();
    private ClusterBackupRequest? _pending;

    /// <summary><c>full</c>, <c>diff</c> or <c>incr</c>; anything else is null.</summary>
    public static string? NormalizeType(string? type)
    {
        var t = (type ?? "incr").Trim().ToLowerInvariant();
        return t is "full" or "diff" or "incr" ? t : null;
    }

    public ClusterBackupRequest? Peek()
    {
        lock (_lock)
        {
            ExpireUnlocked();
            return _pending;
        }
    }

    /// <summary>False when another request is already waiting.</summary>
    public bool TryRequest(string type, string? by, string? targetNode)
    {
        var normalised = NormalizeType(type);
        if (normalised is null) return false;

        lock (_lock)
        {
            ExpireUnlocked();
            if (_pending is not null) return false;
            _pending = new ClusterBackupRequest
            {
                Type = normalised,
                RequestedAt = DateTime.UtcNow,
                RequestedBy = by,
                TargetNode = targetNode,
            };
            return true;
        }
    }

    /// <summary>
    /// Hands the command to this node if it is the intended recipient. Null when
    /// there is nothing waiting, or when this is the wrong machine.
    /// </summary>
    public ClusterBackupRequest? Claim(string node)
    {
        lock (_lock)
        {
            ExpireUnlocked();
            if (_pending is null) return null;
            if (_pending.TargetNode is { } target
                && !string.Equals(target, node, StringComparison.OrdinalIgnoreCase))
                return null;

            var claimed = _pending;
            _pending = null;
            return claimed;
        }
    }

    private void ExpireUnlocked()
    {
        if (_pending is not null && DateTime.UtcNow - _pending.RequestedAt > ExpireAfter)
            _pending = null;
    }
}

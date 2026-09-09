using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Infra;

/// <summary>
/// The last self-report from a database node.
///
/// <para>
/// Patroni's REST API answers the questions it owns — who leads, who streams, how
/// far behind. It cannot answer the ones that live on the machine: whether etcd
/// still has quorum, whether the data partition is filling, whether WAL archiving
/// silently stopped delivering, how old the newest backup is. Those come from
/// <c>check-cluster.sh</c>, which already runs on a five-minute timer, and this row
/// is where its verdict lands.
/// </para>
///
/// <para>
/// <b><see cref="ReceivedAt"/> is the important column.</b> The script publishes on
/// every run, healthy or not, precisely so that silence means something: a node
/// whose last report is forty minutes old is not healthy, it is unheard from, and
/// those must not look alike. Before this the script only spoke up when something
/// was wrong, which would have left the panel unable to tell "fine" from "the
/// check is dead" from "the machine is off".
/// </para>
/// </summary>
public class NodeHealth
{
    /// <summary>Hostname as the node knows itself, e.g. <c>zo-pg-2</c>.</summary>
    [Key]
    [MaxLength(64)]
    public string Node { get; set; } = string.Empty;

    /// <summary><c>healthy</c>, <c>degraded</c> or <c>broken</c> — the script's own verdict.</summary>
    [MaxLength(16)]
    public string Status { get; set; } = "unknown";

    /// <summary>The full human-readable check output, shown verbatim in the panel.</summary>
    public string? Report { get; set; }

    /// <summary>When the node ran the check, by the node's clock.</summary>
    public DateTime CheckedAt { get; set; }

    /// <summary>
    /// When the control plane received it, by its own clock. Staleness is measured
    /// against THIS: the two clocks can disagree, and a node with a wrong clock must
    /// not be able to report itself perpetually fresh.
    /// </summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

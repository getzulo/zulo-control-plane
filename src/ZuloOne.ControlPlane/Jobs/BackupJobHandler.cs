using ZuloOne.ControlPlane.Infra;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.Backup"/>.</summary>
public sealed record BackupPayload(string Type, string? TargetNode);

/// <summary>
/// Records an ad-hoc cluster backup and leaves. The repository node does the work
/// on its next health check — this process cannot run pgBackRest.
/// </summary>
/// <remarks>
/// Deliberately short. A job that waited for the node would occupy
/// <see cref="JobWorker"/> for up to five minutes plus the backup itself, and
/// nothing else in the fleet would move. The request sits on
/// <see cref="ClusterBackupGate"/>; the Activity row is the audit trail.
/// </remarks>
public sealed class BackupJobHandler : IJobHandler
{
    private readonly ClusterBackupGate _gate;

    public BackupJobHandler(ClusterBackupGate gate) => _gate = gate;

    public JobKind Kind => JobKind.Backup;

    public Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<BackupPayload>();
        var type = ClusterBackupGate.NormalizeType(payload?.Type) ?? "incr";
        var target = string.IsNullOrWhiteSpace(payload?.TargetNode) ? null : payload!.TargetNode;

        if (!_gate.TryRequest(type, context.Job.CreatedBy, target))
        {
            if (_gate.Peek() is not null)
                throw new InvalidOperationException(
                    "A cluster backup is already waiting for the repository node.");
            throw new InvalidOperationException($"'{payload?.Type}' is not a pgBackRest backup type.");
        }

        var who = target ?? "the repository node";
        return context.StepAsync(
            $"Asked {who} for a {type} backup. It runs on the next health check (up to 5 minutes).",
            100, ct);
    }
}

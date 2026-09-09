using System.Text.Json;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// A job's handle on itself while it runs: report a step, append a line, read the
/// arguments it was queued with.
///
/// Every write here hits the database immediately rather than at the end. That is
/// the entire point — the operator is watching a restore that takes minutes, and
/// progress buffered until completion is progress nobody can see.
/// </summary>
public sealed class JobContext
{
    /// <summary>
    /// The log is a diagnostic tail, not an archive. Postgres would happily store
    /// megabytes, but the panel renders this in a drawer and a runaway loop must not
    /// be able to turn one row into something that stalls the fleet list.
    /// </summary>
    private const int MaxLogChars = 64 * 1024;

    private readonly ControlPlaneDbContext _db;

    public JobContext(ControlPlaneDbContext db, Job job)
    {
        _db = db;
        Job = job;
    }

    public Job Job { get; }

    /// <summary>Names the current step and where it sits, then persists both.</summary>
    public async Task StepAsync(string step, int progress, CancellationToken ct = default)
    {
        Job.Step = step;
        Job.Progress = Math.Clamp(progress, 0, 100);
        await AppendAsync($"── {step}", ct);
    }

    /// <summary>Appends one line of detail beneath the current step.</summary>
    public Task LogAsync(string line, CancellationToken ct = default) => AppendAsync(line, ct);

    private async Task AppendAsync(string line, CancellationToken ct)
    {
        var stamped = $"{DateTime.UtcNow:HH:mm:ss}  {line}";
        Job.Log = string.IsNullOrEmpty(Job.Log) ? stamped : $"{Job.Log}\n{stamped}";

        // Trim from the FRONT: when a job goes wrong the interesting part is what it
        // was doing most recently, not how it started.
        if (Job.Log.Length > MaxLogChars)
            Job.Log = "… (earlier output trimmed) …\n" + Job.Log[^MaxLogChars..];

        // CancellationToken.None: progress is also how a cancellation gets recorded,
        // so writing it must not itself depend on the token still being live.
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The arguments this job was queued with, or null when it needs none.</summary>
    public T? Payload<T>() where T : class
        => string.IsNullOrWhiteSpace(Job.Payload) ? null : JsonSerializer.Deserialize<T>(Job.Payload);
}

/// <summary>
/// Runs one kind of job. One implementation per <see cref="JobKind"/>, resolved by
/// the worker from the DI container — so adding a kind is adding a class, not
/// editing a switch in the worker.
/// </summary>
public interface IJobHandler
{
    JobKind Kind { get; }

    /// <summary>
    /// Does the work. Throwing marks the job Failed with the exception's message;
    /// returning normally marks it Succeeded. The handler owns any rollback of its
    /// own side effects — the worker only records the outcome.
    /// </summary>
    Task RunAsync(JobContext context, CancellationToken ct);
}

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// The wake-up signal for <see cref="JobWorker"/>. Singleton, and deliberately
/// holds nothing but ids — the job itself lives in the database, so a lost signal
/// costs a delay rather than the work.
/// </summary>
public sealed class JobChannel
{
    // Unbounded is safe: the only producer is an authenticated operator acting by
    // hand, and there is no path that can flood it.
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Notify(Guid jobId) => _channel.Writer.TryWrite(jobId);
}

public interface IJobQueue
{
    /// <summary>Records the job and wakes the worker. Returns immediately.</summary>
    Task<Job> EnqueueAsync(
        JobKind kind, Guid? tenantId, string? tenantSlug,
        object? payload = null, string? createdBy = null, CancellationToken ct = default);
}

public sealed class JobQueue : IJobQueue
{
    private readonly ControlPlaneDbContext _db;
    private readonly JobChannel _channel;

    public JobQueue(ControlPlaneDbContext db, JobChannel channel)
    {
        _db = db;
        _channel = channel;
    }

    public async Task<Job> EnqueueAsync(
        JobKind kind, Guid? tenantId, string? tenantSlug,
        object? payload = null, string? createdBy = null, CancellationToken ct = default)
    {
        var job = new Job
        {
            Kind = kind,
            TenantId = tenantId,
            TenantSlug = tenantSlug,
            CreatedBy = createdBy,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload),
            State = JobState.Queued,
        };

        _db.Jobs.Add(job);
        // Written BEFORE the signal, so the worker can never be woken for a row that
        // is not there yet.
        await _db.SaveChangesAsync(ct);
        _channel.Notify(job.Id);
        return job;
    }
}

/// <summary>
/// Runs queued jobs, one at a time, off the request that asked for them.
///
/// <para>
/// <b>Serial by design.</b> Two tenants building at once means two first-boot
/// metadata compiles competing for one host's CPU, which makes both slower and can
/// push either past the readiness timeout. It also gives mutual exclusion for free
/// where it is actually required — a restore must not overlap a backup, and neither
/// should overlap provisioning, because all three lean on the same database host.
/// </para>
///
/// <para>
/// The queue is durable because the jobs are rows. The channel only carries a
/// nudge; on start the worker sweeps the table, so a job queued moments before a
/// restart still runs, and one that was mid-flight is recorded as interrupted
/// rather than left claiming to be running forever.
/// </para>
/// </summary>
public sealed class JobWorker : BackgroundService
{
    private readonly JobChannel _channel;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<JobWorker> _logger;

    public JobWorker(JobChannel channel, IServiceScopeFactory scopes, ILogger<JobWorker> logger)
    {
        _channel = channel;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
            await DrainAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown. ReadAllAsync throws when its token is cancelled, and
            // BackgroundServiceExceptionBehavior defaults to StopHost — so letting
            // this escape makes every clean stop log as "a BackgroundService has
            // thrown an unhandled exception" and buries whatever actually caused the
            // shutdown underneath it. That is exactly how a certificate the container
            // could not read once presented as a crashing worker.
        }
    }

    /// <summary>
    /// Reconciles the table with reality at start: anything that says Running was
    /// interrupted by whatever stopped the last process, and anything still Queued
    /// never got its signal because the channel does not survive a restart.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var interrupted = await db.Jobs.Where(j => j.State == JobState.Running).ToListAsync(ct);
        foreach (var job in interrupted)
        {
            job.State = JobState.Failed;
            job.Error = "Interrupted by a control-plane restart. Check for a half-built tenant or a leftover scratch directory before retrying.";
            job.FinishedAt = DateTime.UtcNow;
            _logger.LogWarning("Job {JobId} ({Kind}) was running when the service last stopped — marked failed", job.Id, job.Kind);
        }

        var pending = await db.Jobs.Where(j => j.State == JobState.Queued)
            .OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync(ct);

        if (interrupted.Count > 0) await db.SaveChangesAsync(ct);
        foreach (var id in pending) _channel.Notify(id);
        if (pending.Count > 0)
            _logger.LogInformation("Requeued {Count} job(s) that were pending across the restart", pending.Count);
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            // A scope per job: ControlPlaneDbContext is scoped, and one long-lived
            // context would accumulate tracked entities across every job the service
            // ever runs.
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
            if (job is null)
            {
                _logger.LogWarning("Job {JobId} was signalled but is no longer in the registry", jobId);
                continue;
            }
            // Requeued on start AND signalled at creation, so the same id can arrive
            // twice. Whoever gets there first wins.
            if (job.State != JobState.Queued) continue;

            var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Kind == job.Kind);
            if (handler is null)
            {
                job.State = JobState.Failed;
                job.Error = $"No handler is registered for job kind {job.Kind}.";
                job.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogError("Job {JobId} has kind {Kind}, which nothing handles", job.Id, job.Kind);
                continue;
            }

            job.State = JobState.Running;
            job.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(stoppingToken);

            var context = new JobContext(db, job);
            try
            {
                await handler.RunAsync(context, stoppingToken);

                job.State = JobState.Succeeded;
                job.Progress = 100;
                job.Step = "Done";
                job.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                _logger.LogInformation("Job {JobId} ({Kind}) succeeded", job.Id, job.Kind);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The service is going away mid-job. Record it and RETURN rather than
                // rethrow: an exception leaving this method is treated as fatal by
                // BackgroundServiceExceptionBehavior, which would turn an ordinary
                // shutdown into something that reads like a crash.
                job.State = JobState.Cancelled;
                job.Error = "The control plane shut down while this job was running.";
                job.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                _logger.LogWarning("Job {JobId} ({Kind}) was cut short by shutdown", job.Id, job.Kind);
                return;
            }
            catch (Exception ex)
            {
                // CancellationToken.None: the commonest way to land here is a
                // cancelled token, and the record of the failure must not depend on
                // the token that failed.
                job.State = JobState.Failed;
                job.Error = ex.Message;
                job.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                // Logged and swallowed: one job's failure must not stop the queue.
                _logger.LogError(ex, "Job {JobId} ({Kind}) failed", job.Id, job.Kind);
            }
        }
    }
}

using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Settings;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// Puts a <see cref="JobKind.Prune"/> on the queue every so often, and does
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The deliberate part is how little this does. Sweeping from inside a timer
/// would run the deletes concurrently with whatever <see cref="JobWorker"/> is
/// doing, and the queue's single-file execution is the ONLY thing separating a
/// delete from the <c>pg_dump</c> writing that file or the <c>pg_restore</c>
/// reading it. Enqueuing instead buys serialisation, a durable record, progress
/// and an operator-visible log — none of which a timer has.
/// </para>
///
/// <para>
/// It also refuses to queue a second prune while one is already waiting. The
/// interval is hours and a prune takes seconds, so a backlog could only mean the
/// worker is stuck — and piling identical jobs behind a stuck worker turns one
/// problem into a queue nobody can read.
/// </para>
/// </remarks>
public sealed class PruneScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SettingsStore _settings;
    private readonly ILogger<PruneScheduler> _logger;

    public PruneScheduler(IServiceScopeFactory scopes, SettingsStore settings, ILogger<PruneScheduler> logger)
    {
        _scopes = scopes;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not at startup: a panel restarting during an incident should come up and
        // answer questions, not begin deleting things. The first sweep is one
        // interval away.
        while (!stoppingToken.IsCancellationRequested)
        {
            var hours = Math.Clamp(_settings.Int("Snapshots:PruneIntervalHours"), 1, 168);
            try
            {
                await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                await QueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // A scheduler that dies stops sweeping for ever, and nothing would
                // say so until the disk filled.
                _logger.LogError(ex, "Could not queue the snapshot prune; trying again in {Hours}h.", hours);
            }
        }
    }

    private async Task QueueAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var pending = await db.Jobs.AnyAsync(
            j => j.Kind == JobKind.Prune && (j.State == JobState.Queued || j.State == JobState.Running), ct);
        if (pending)
        {
            _logger.LogInformation("A prune is already queued or running; not adding another.");
            return;
        }

        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        await queue.EnqueueAsync(JobKind.Prune, tenantId: null, tenantSlug: null, createdBy: "schedule", ct: ct);
    }
}

using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.Rollout"/>.</summary>
/// <param name="ImageTag">Null leaves each tenant on the image it already runs.</param>
/// <param name="SetModels">Whether <paramref name="Models"/> should be applied at all.</param>
/// <param name="Models">The new pin as a JSON list of names; null means "follow the fleet default".</param>
/// <param name="TenantIds">In order. The wave walks them one at a time.</param>
/// <param name="StopOnFailure">Halt the wave on the first tenant that does not come up.</param>
/// <param name="StopOnCompileErrors">
/// Halt when a tenant comes up but its models do not build. Separate from
/// <paramref name="StopOnFailure"/> because they are different accidents: one is an
/// image that will not run, the other is one that runs and does not work.
/// </param>
public sealed record RolloutPayload(
    string? ImageTag,
    bool SetModels,
    string? Models,
    Guid[] TenantIds,
    bool StopOnFailure = true,
    bool StopOnCompileErrors = true);

/// <summary>
/// Moves several tenants, one at a time, checking each before starting the next.
/// </summary>
/// <remarks>
/// <para>
/// The whole value is the pause between tenants. A bad image reaching the second
/// tenant must not reach the twentieth, and that decision is the only thing N
/// independent upgrade jobs could not make — each would succeed or fail alone with
/// nothing watching the sequence.
/// </para>
///
/// <para>
/// Each tenant gets its own slice of the progress bar rather than reporting 0→100,
/// so the bar advances once across the wave instead of resetting per tenant.
/// </para>
///
/// <para>
/// Cancellation is checked BETWEEN tenants, never during one. A wave stopped
/// mid-recreate would leave a tenant with no container; stopped between them it
/// leaves every tenant either moved or untouched, which is a state somebody can act
/// on.
/// </para>
/// </remarks>
public sealed class RolloutJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantUpgradeService _upgrades;

    public RolloutJobHandler(ControlPlaneDbContext db, TenantUpgradeService upgrades)
    {
        _db = db;
        _upgrades = upgrades;
    }

    public JobKind Kind => JobKind.Rollout;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<RolloutPayload>()
            ?? throw new InvalidOperationException("A Rollout job must name the tenants to move.");
        if (payload.TenantIds.Length == 0)
            throw new InvalidOperationException("A Rollout job must name at least one tenant.");

        var target = payload.ImageTag is { Length: > 0 } tag ? tag : "their current image";
        await context.LogAsync(
            $"Rolling {payload.TenantIds.Length} tenant(s) onto {target}"
            + (payload.SetModels ? $" with models {payload.Models ?? "(fleet default)"}" : "")
            + ". One at a time; the wave stops on the first that does not come up.", ct);

        var moved = new List<string>();
        var failed = new List<string>();
        var warned = new List<string>();
        string? stoppedBecause = null;

        for (var i = 0; i < payload.TenantIds.Length; i++)
        {
            // Between tenants, never inside one: see the remarks above.
            if (await CancelRequestedAsync(context.Job.Id, ct))
            {
                stoppedBecause = $"cancelled by an operator after {moved.Count} of {payload.TenantIds.Length}";
                break;
            }

            var from = i * 100 / payload.TenantIds.Length;
            var to = (i + 1) * 100 / payload.TenantIds.Length;

            var id = payload.TenantIds[i];
            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null)
            {
                // Gone since the wave was planned. Not a failure of the rollout.
                await context.LogAsync($"Tenant {id} is no longer in the registry — skipped.", ct);
                continue;
            }

            var outcome = await _upgrades.MoveAsync(
                tenant, payload.ImageTag, payload.SetModels, payload.Models, context, from, to, ct);

            if (!outcome.Succeeded)
            {
                failed.Add($"{tenant.Slug}: {outcome.Error}");
                await context.LogAsync($"FAILED {tenant.Slug}: {outcome.Error}", ct);
                if (payload.StopOnFailure)
                {
                    stoppedBecause = $"{tenant.Slug} did not come up";
                    break;
                }
                continue;
            }

            moved.Add(tenant.Slug);

            if (outcome.CompilationProblems.Count > 0)
            {
                warned.Add($"{tenant.Slug}: {string.Join(" | ", outcome.CompilationProblems.Take(5))}");
                await context.LogAsync(
                    $"{tenant.Slug} is serving, but {outcome.CompilationProblems.Count} model(s) do not compile.", ct);
                if (payload.StopOnCompileErrors)
                {
                    stoppedBecause = $"{tenant.Slug} came up with models that do not compile";
                    break;
                }
            }
        }

        var untouched = payload.TenantIds.Length - moved.Count - failed.Count;
        await context.LogAsync(
            $"Moved {moved.Count}, failed {failed.Count}, not started {untouched}."
            + (moved.Count > 0 ? $" Moved: {string.Join(", ", moved)}." : ""), ct);

        if (stoppedBecause is not null)
        {
            // Thrown so the job records as Failed and the wave is visible as stopped.
            // The tenants already moved stay moved — they are serving, and rolling
            // them back would be a second unrequested change.
            throw new InvalidOperationException(
                $"Rollout stopped: {stoppedBecause}. {moved.Count} tenant(s) were moved and are serving; "
                + $"{untouched} were not started."
                + (failed.Count > 0 ? $" {string.Join(" ", failed)}" : "")
                + (warned.Count > 0 ? $" Compilation: {string.Join(" ", warned)}" : ""));
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Rollout finished with {failed.Count} failure(s): {string.Join(" ", failed)}");
        }

        await context.StepAsync($"Moved {moved.Count} tenant(s)", 100, ct);
    }

    /// <summary>
    /// Reads the flag straight from the database, not from the job entity the context
    /// carries — that one was loaded when the job started and can never show a request
    /// made since.
    /// </summary>
    private async Task<bool> CancelRequestedAsync(Guid jobId, CancellationToken ct) =>
        await _db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.CancelRequested).FirstOrDefaultAsync(ct);
}

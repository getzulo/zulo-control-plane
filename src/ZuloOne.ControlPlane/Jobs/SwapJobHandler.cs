using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Arguments for a <see cref="JobKind.Swap"/>.</summary>
public sealed record SwapPayload(Guid ScratchTenantId);

/// <summary>
/// Moves a verified restored database under the live tenant.
///
/// <para>
/// The live tenant keeps everything that identifies it — slug, hostname, role,
/// password, signing key, container configuration. Only the data underneath
/// changes. Its container therefore restarts with the connection string it already
/// had, and every session token issued before the swap stays valid.
/// </para>
///
/// <para>
/// The database being displaced is renamed aside, not dropped. That rename is the
/// undo, it is instant, and it is discarded by a separate deliberate action.
/// </para>
/// </summary>
public sealed class SwapJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantDatabaseProvisioner _databases;
    private readonly TenantContainerService _containers;
    private readonly TenantHealthProbe _health;
    private readonly FleetConfig _fleet;

    public SwapJobHandler(
        ControlPlaneDbContext db,
        TenantDatabaseProvisioner databases,
        TenantContainerService containers,
        TenantHealthProbe health,
        FleetConfig fleet)
    {
        _db = db;
        _databases = databases;
        _containers = containers;
        _health = health;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.Swap;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<SwapPayload>()
            ?? throw new InvalidOperationException("A Swap job must name the restored copy to swap in.");

        var liveId = context.Job.TenantId ?? throw new InvalidOperationException("A Swap job must name the live tenant.");
        var live = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == liveId, ct)
            ?? throw new InvalidOperationException("The live tenant is no longer in the registry.");
        var scratch = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == payload.ScratchTenantId, ct)
            ?? throw new InvalidOperationException("The restored copy is no longer in the registry.");

        await context.StepAsync("Checking both sides", 5, ct);
        if (string.IsNullOrWhiteSpace(live.DatabaseName) || string.IsNullOrWhiteSpace(live.DatabaseRole))
            throw new InvalidOperationException($"'{live.Slug}' has no database to replace.");
        if (string.IsNullOrWhiteSpace(scratch.DatabaseName) || string.IsNullOrWhiteSpace(scratch.DatabaseRole))
            throw new InvalidOperationException($"'{scratch.Slug}' has no database to swap in.");
        if (scratch.Id == live.Id)
            throw new InvalidOperationException("A tenant cannot be swapped with itself.");
        // The copy having booted is the ONLY evidence that this dump can carry the
        // tenant forward. Swapping in one that never answered would replace working
        // data with data known not to work.
        if (scratch.Status != TenantStatus.Active)
            throw new InvalidOperationException(
                $"'{scratch.Slug}' is {scratch.Status}, not Active — only a copy that came up healthy may be swapped in.");
        // Not fatal, but the operator should have looked. Refusing here would block
        // a legitimate restore of a tenant that is down precisely because its data
        // is broken, which is the commonest reason to be doing this at all.
        if (scratch.RestoredFromSlug is not null && scratch.RestoredFromSlug != live.Slug)
            await context.LogAsync(
                $"Note: this copy was restored from '{scratch.RestoredFromSlug}', not '{live.Slug}'.", ct);

        var archive = ArchiveName(live.DatabaseName!);
        await context.LogAsync(
            $"'{live.DatabaseName}' will be kept as '{archive}'. Undo is: " +
            $"stop {live.Slug}, DROP DATABASE \"{live.DatabaseName}\", " +
            $"ALTER DATABASE \"{archive}\" RENAME TO \"{live.DatabaseName}\", start {live.Slug}.", ct);

        await context.StepAsync("Stopping both containers", 20, ct);
        // Stopped, not killed: the tenant gets its 30 seconds to finish in-flight
        // work. A rename cannot proceed while a session is attached anyway.
        if (!string.IsNullOrWhiteSpace(live.ContainerId))
            await _containers.StopAsync(live.ContainerId!, ct);
        if (!string.IsNullOrWhiteSpace(scratch.ContainerId))
            await _containers.StopAsync(scratch.ContainerId!, ct);

        await context.StepAsync("Swapping the databases", 40, ct);
        await _databases.SwapAsync(
            live.DatabaseName!, live.DatabaseRole!,
            scratch.DatabaseName!, scratch.DatabaseRole!,
            archive, ct);

        live.PreviousDatabaseName = archive;
        live.PreviousDatabaseAt = DateTime.UtcNow;
        // The scratch row no longer owns a database — it was renamed out from under
        // it — so record that before anything can try to drop it by name.
        scratch.DatabaseName = null;
        await _db.SaveChangesAsync(ct);

        await context.StepAsync($"Starting {live.Slug}", 60, ct);
        // The SAME connection string as before: same database name, same role, same
        // password. Nothing about the container's configuration changed, so there is
        // nothing to recreate.
        await _containers.StartAsync(live.ContainerId!, ct);

        await context.StepAsync($"Waiting for {live.Slug} to answer", 75, ct);
        var ready = await _health.WaitUntilReadyAsync(
            _containers.HostFor(live.Slug), TimeSpan.FromSeconds(_fleet.ReadinessTimeoutSeconds), ct);
        if (!ready)
        {
            live.Health = TenantHealth.Down;
            live.LastError = $"Did not come up after the swap. The previous database is intact as '{archive}'.";
            await _db.SaveChangesAsync(CancellationToken.None);
            throw new TimeoutException(live.LastError);
        }

        live.Status = TenantStatus.Active;
        live.Health = TenantHealth.Ok;
        live.LastHealthAt = DateTime.UtcNow;
        live.LastError = null;
        await _db.SaveChangesAsync(ct);

        await context.StepAsync("Discarding the copy", 90, ct);
        // The copy has served its purpose. Its DATABASE is now the live one, so only
        // the container, the role and the registry row remain to remove.
        try
        {
            if (!string.IsNullOrWhiteSpace(scratch.ContainerId))
                await _containers.RemoveAsync(scratch.ContainerId!, ct);
            await _databases.DropAsync(null, scratch.DatabaseRole, ct);
            _db.Tenants.Remove(scratch);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The swap itself succeeded, and that is what matters. Leftovers are
            // untidy, not dangerous — say so instead of failing a completed job.
            await context.LogAsync(
                $"The swap succeeded, but clearing away '{scratch.Slug}' did not finish: {ex.Message}", ct);
        }

        await context.StepAsync($"{live.Slug} is running on the restored data", 100, ct);
        await context.LogAsync(
            $"The previous database is kept as '{archive}'. Discard it when you are satisfied — until then it costs disk.", ct);
    }

    /// <summary>
    /// <c>tenant_acme_pre_20260909T031500Z</c>. Timestamped rather than a fixed
    /// suffix, so a second restore cannot silently overwrite the undo left by the
    /// first — and so the name says WHEN, which is the question anyone finding it
    /// months later will ask.
    /// </summary>
    private static string ArchiveName(string database)
    {
        var name = $"{database}_pre_{DateTime.UtcNow:yyyyMMddTHHmmss}Z";
        // PostgreSQL truncates identifiers at 63 bytes, and a truncated name could
        // collide with a previous archive. Trim the STEM, never the timestamp.
        if (name.Length > 63)
        {
            var suffix = $"_pre_{DateTime.UtcNow:yyyyMMddTHHmmss}Z";
            name = database[..(63 - suffix.Length)] + suffix;
        }
        return name;
    }
}

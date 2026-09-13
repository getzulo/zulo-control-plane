using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

/// <summary>
/// Farm journal: who occupies how much Mongo, the same event viewer as the
/// tenant page, and the shovel (TTL / purge). Not the Docker stdout tail on
/// the tenant card — that stays for bootstrap before the sink is up.
/// </summary>
[ApiController]
[Route("api/logs")]
[Produces("application/json")]
public sealed class LogsController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantLogStore _store;

    public LogsController(ControlPlaneDbContext db, TenantLogStore store)
    {
        _db = db;
        _store = store;
    }

    [HttpGet("status")]
    public async Task<LogStoreStatusDto> Status(CancellationToken ct) =>
        await _store.StatusAsync(await TenantsAsync(ct), ct);

    [HttpGet("tenants")]
    public async Task<IReadOnlyList<LogTenantRowDto>> Tenants(CancellationToken ct) =>
        await _store.FleetAsync(await TenantsAsync(ct), ct);

    [HttpGet("events")]
    public async Task<LogEventsResponse> Events(
        [FromQuery] string? slug,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? minLevel,
        [FromQuery] string? sourceContains,
        [FromQuery] string? channel,
        [FromQuery] string? text,
        [FromQuery] string? userName,
        [FromQuery] string? requestId,
        [FromQuery] Guid? jobId,
        [FromQuery] Guid? agentId,
        [FromQuery] DateTime? cursorTimestamp,
        [FromQuery] string? cursorId,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var items = await _store.QueryAsync(
            await TenantsAsync(ct),
            slug,
            fromUtc ?? DateTime.UtcNow.AddHours(-24),
            toUtc ?? DateTime.UtcNow,
            minLevel,
            sourceContains,
            channel,
            text,
            userName,
            requestId,
            jobId,
            agentId,
            cursorTimestamp,
            cursorId,
            take,
            ct);
        return new LogEventsResponse(items, Math.Clamp(take, 1, TenantLogStore.MaxTake));
    }

    [HttpPost("{slug}/purge")]
    public async Task<IActionResult> Purge(string slug, [FromBody] LogPurgeRequest body, CancellationToken ct)
    {
        if (await FindAsync(slug, ct) is null) return NotFound();
        try
        {
            await _store.PurgeAsync(slug, body, ct);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{slug}/ttl")]
    public async Task<IActionResult> Ttl(string slug, [FromBody] LogTtlRequest body, CancellationToken ct)
    {
        if (await FindAsync(slug, ct) is null) return NotFound();
        try
        {
            await _store.SetTtlAsync(slug, body.Days, ct);
            return Ok(new { ok = true, days = body.Days });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (MongoCommandException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private Task<List<Tenant>> TenantsAsync(CancellationToken ct) =>
        _db.Tenants.AsNoTracking().OrderBy(t => t.Slug).ToListAsync(ct);

    private Task<Tenant?> FindAsync(string slug, CancellationToken ct) =>
        _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug, ct);
}

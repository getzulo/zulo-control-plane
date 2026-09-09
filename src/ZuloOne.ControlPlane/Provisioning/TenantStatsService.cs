using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>What a tenant is costing right now, on both sides.</summary>
public sealed record TenantStats(
    ContainerStats? Container,
    DatabaseStats? Database,
    string? Error);

public sealed record ContainerStats(
    double CpuPercent,
    long MemoryBytes,
    long MemoryLimitBytes,
    double MemoryPercent,
    DateTime? StartedAt,
    int RestartCount,
    string State,
    long NetworkRxBytes,
    long NetworkTxBytes);

public sealed record DatabaseStats(
    long SizeBytes,
    int Connections,
    int MaxConnections,
    int TableCount,
    long? LargestTableBytes,
    string? LargestTableName);

/// <summary>
/// Live resource use for one tenant: the container from the Docker API, the
/// database from Postgres.
///
/// <para>
/// Both are read on demand rather than collected. A fleet this size does not need
/// a time-series database to answer "what is this tenant doing right now", and the
/// panel asking directly cannot drift from reality the way a scraper can.
/// </para>
/// </summary>
public sealed class TenantStatsService
{
    private readonly IDockerClient _docker;
    private readonly TenantDatabaseSettings _db;
    private readonly ILogger<TenantStatsService> _logger;

    public TenantStatsService(
        IDockerClient docker, IOptions<TenantDatabaseSettings> db, ILogger<TenantStatsService> logger)
    {
        _docker = docker;
        _db = db.Value;
        _logger = logger;
    }

    public async Task<TenantStats> ReadAsync(Registry.Tenant tenant, CancellationToken ct = default)
    {
        ContainerStats? container = null;
        DatabaseStats? database = null;
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(tenant.ContainerId))
        {
            try { container = await ReadContainerAsync(tenant.ContainerId!, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read container stats for {Slug}", tenant.Slug);
                errors.Add($"container: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(tenant.DatabaseName))
        {
            try { database = await ReadDatabaseAsync(tenant, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read database stats for {Slug}", tenant.Slug);
                errors.Add($"database: {ex.Message}");
            }
        }

        // Partial answers are useful: a tenant whose container is gone but whose
        // database is fine is a specific, actionable situation, and returning
        // nothing at all would hide which half is broken.
        return new TenantStats(container, database, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private async Task<ContainerStats> ReadContainerAsync(string containerId, CancellationToken ct)
    {
        var inspect = await _docker.Containers.InspectContainerAsync(containerId, ct);

        // TWO samples, ~1s apart. Docker's one-shot stats returns precpu_stats
        // zeroed, so a CPU percentage computed from it is meaningless — the usual
        // result being a wild number on the first read and nothing to compare
        // against. Streaming briefly and taking the second reading is the only way
        // to get a real delta.
        var readings = new List<ContainerStatsResponse>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new Progress<ContainerStatsResponse>(r =>
        {
            readings.Add(r);
            if (readings.Count >= 2) window.Cancel();
        });

        try
        {
            window.CancelAfter(TimeSpan.FromSeconds(4));
            await _docker.Containers.GetContainerStatsAsync(
                containerId, new ContainerStatsParameters { Stream = true }, progress, window.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Expected: this is how the stream is stopped once two samples are in.
        }

        var last = readings.LastOrDefault();
        var cpu = 0.0;
        if (readings.Count >= 2 && last is not null)
        {
            var prev = readings[^2];
            var cpuDelta = (double)last.CPUStats.CPUUsage.TotalUsage - prev.CPUStats.CPUUsage.TotalUsage;
            var sysDelta = (double)last.CPUStats.SystemUsage - prev.CPUStats.SystemUsage;
            var cores = last.CPUStats.OnlineCPUs > 0
                ? last.CPUStats.OnlineCPUs
                : (uint)(last.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);
            if (sysDelta > 0 && cpuDelta > 0) cpu = cpuDelta / sysDelta * cores * 100.0;
        }

        // Docker reports memory INCLUDING the page cache, which for a .NET process
        // reading its own assemblies overstates what the tenant is really holding.
        // Subtracting the cache is what `docker stats` itself shows.
        var used = (long)(last?.MemoryStats.Usage ?? 0);
        if (last?.MemoryStats.Stats is not null && last.MemoryStats.Stats.TryGetValue("cache", out var cache))
            used -= (long)cache;
        var limit = (long)(last?.MemoryStats.Limit ?? 0);

        long rx = 0, tx = 0;
        foreach (var n in last?.Networks?.Values ?? [])
        {
            rx += (long)n.RxBytes;
            tx += (long)n.TxBytes;
        }

        return new ContainerStats(
            Math.Round(cpu, 2),
            Math.Max(0, used),
            limit,
            limit > 0 ? Math.Round(Math.Max(0, used) * 100.0 / limit, 1) : 0,
            // Docker.DotNet surfaces this as a string on this version, and it is
            // "0001-01-01T00:00:00Z" for a container that has never run — which
            // must read as "no start time" rather than as the year 1.
            DateTime.TryParse(inspect.State?.StartedAt, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var started) && started.Year > 1
                ? started
                : null,
            (int)inspect.RestartCount,
            inspect.State?.Status ?? "unknown",
            rx, tx);
    }

    private async Task<DatabaseStats> ReadDatabaseAsync(Registry.Tenant tenant, CancellationToken ct)
    {
        var ssl = _db.RequireSsl ? "SSL Mode=Require;Trust Server Certificate=true;" : string.Empty;
        var target = _db.Host.Contains(',') ? "Target Session Attributes=Primary;" : string.Empty;
        var connectionString =
            $"Host={_db.Host};Port={_db.Port};Database={tenant.DatabaseName};" +
            $"Username={_db.AdminUser};Password={_db.AdminPassword};{ssl}Pooling=false;{target}";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // One round trip. Each of these is cheap on its own, but the panel reads
        // them per tenant on a page that may show several.
        const string sql = """
            SELECT pg_database_size(current_database()),
                   (SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()),
                   (SELECT setting::int FROM pg_settings WHERE name = 'max_connections'),
                   (SELECT count(*) FROM pg_tables WHERE schemaname = 'public'),
                   (SELECT pg_total_relation_size(c.oid) FROM pg_class c
                     JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relkind = 'r'
                    ORDER BY pg_total_relation_size(c.oid) DESC LIMIT 1),
                   (SELECT c.relname FROM pg_class c
                     JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relkind = 'r'
                    ORDER BY pg_total_relation_size(c.oid) DESC LIMIT 1)
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("No statistics returned.");

        return new DatabaseStats(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }
}

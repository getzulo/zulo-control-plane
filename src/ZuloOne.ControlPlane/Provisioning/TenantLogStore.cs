using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using ZuloOne.ControlPlane.Api;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Reads and maintains per-tenant <c>logs_{slug}</c> databases. The control plane
/// never goes through a tenant API for this: a stopped container must not hide
/// its journal, and a fan-out across live sidecars is the failure mode the spec
/// rejected.
/// </summary>
public sealed class TenantLogStore
{
    public const int MaxTake = 200;
    private static readonly TimeSpan StatsTtl = TimeSpan.FromSeconds(45);

    private static readonly string[] LevelOrder =
        ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    private readonly TenantLogSettings _settings;
    private readonly IMongoClient? _client;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantLogStore> _logger;

    public TenantLogStore(
        IOptions<TenantLogSettings> settings,
        IMemoryCache cache,
        ILogger<TenantLogStore> logger)
    {
        _settings = settings.Value;
        _cache = cache;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(_settings.Url))
            _client = new MongoClient(_settings.Url);
    }

    public bool IsConfigured => _client is not null;

    public async Task<LogStoreStatusDto> StatusAsync(IReadOnlyList<Tenant> tenants, CancellationToken ct)
    {
        if (_client is null)
            return new LogStoreStatusDto("disconnected", null, null, tenants.Count, tenants.Count);

        try
        {
            var fleet = await FleetAsync(tenants, ct);
            var missing = fleet.Count(r => !r.DatabaseExists);
            var size = fleet.Sum(r => r.SizeBytes ?? 0);
            return new LogStoreStatusDto("connected", null, size, tenants.Count, missing);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mongo journal is configured but not reachable");
            return new LogStoreStatusDto("disconnected", ex.Message, null, tenants.Count, tenants.Count);
        }
    }

    public async Task<IReadOnlyList<LogTenantRowDto>> FleetAsync(IReadOnlyList<Tenant> tenants, CancellationToken ct)
    {
        if (_client is null) return tenants.Select(MissingRow).ToList();

        HashSet<string> existing;
        try
        {
            var names = await _client.ListDatabaseNamesAsync(ct);
            existing = new HashSet<string>(await names.ToListAsync(ct), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Same contract as StatusAsync: the Logs page stays up when mongod is
            // down. An uncaught timeout here was a 500 on GET /api/logs/tenants.
            _logger.LogWarning(ex, "Mongo journal is configured but not reachable");
            return tenants.Select(MissingRow).ToList();
        }

        var rows = new List<LogTenantRowDto>(tenants.Count);
        foreach (var tenant in tenants)
        {
            var dbName = TenantLogNames.Database(tenant.Slug);
            if (!existing.Contains(dbName))
            {
                rows.Add(MissingRow(tenant));
                continue;
            }

            rows.Add(await _cache.GetOrCreateAsync(
                $"log-stats:{tenant.Slug}",
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = StatsTtl;
                    return await ReadRowAsync(tenant, ct);
                }) ?? MissingRow(tenant));
        }

        return rows.OrderByDescending(r => r.SizeBytes ?? -1).ToList();
    }

    public async Task<IReadOnlyList<LogEventDto>> QueryAsync(
        IReadOnlyList<Tenant> tenants,
        string? slug,
        DateTime fromUtc,
        DateTime toUtc,
        string? minLevel,
        string? sourceContains,
        string? channel,
        string? text,
        string? userName,
        string? requestId,
        Guid? jobId,
        Guid? agentId,
        DateTime? cursorTimestamp,
        string? cursorId,
        int take,
        CancellationToken ct)
    {
        take = Math.Clamp(take, 1, MaxTake);
        if (_client is null) return [];

        var targets = string.IsNullOrWhiteSpace(slug)
            ? tenants
            : tenants.Where(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase)).ToList();

        var filter = BuildFilter(
            fromUtc, toUtc, minLevel, sourceContains, channel, text,
            userName, requestId, jobId, agentId, cursorTimestamp, cursorId);

        var bags = new List<LogEventDto>();
        foreach (var tenant in targets)
        {
            try
            {
                var col = Collection(tenant.Slug);
                var found = await col.Find(filter)
                    .Sort(Builders<BsonDocument>.Sort.Descending("Timestamp").Descending("_id"))
                    .Limit(take)
                    .ToListAsync(ct);
                bags.AddRange(found.Select(d => ToEvent(d, tenant.Slug)));
            }
            catch (MongoCommandException)
            {
                // Database or collection is missing — that tenant simply has no journal yet.
            }
        }

        return bags
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    public async Task PurgeAsync(string slug, LogPurgeRequest body, CancellationToken ct)
    {
        if (!string.Equals(body.ConfirmSlug, slug, StringComparison.Ordinal))
            throw new InvalidOperationException("Type the tenant slug to confirm.");
        if (_client is null) throw new InvalidOperationException("Journal is not connected.");

        var col = Collection(slug);
        if (body.All)
        {
            await col.Database.DropCollectionAsync(TenantLogNames.Collection, ct);
            await EnsureIndexesAsync(col, _settings.TtlDays, ct);
            Invalidate(slug);
            return;
        }

        var days = body.OlderThanDays ?? _settings.TtlDays;
        if (days < 1) throw new InvalidOperationException("olderThanDays must be at least 1.");
        var cutoff = DateTime.UtcNow.AddDays(-days);
        await col.DeleteManyAsync(
            Builders<BsonDocument>.Filter.Lt("Timestamp", cutoff), ct);
        Invalidate(slug);
    }

    public async Task SetTtlAsync(string slug, int days, CancellationToken ct)
    {
        if (days < 1 || days > 365) throw new InvalidOperationException("TTL must be 1–365 days.");
        if (_client is null) throw new InvalidOperationException("Journal is not connected.");

        var col = Collection(slug);
        // Core 2026.9.20 creates this TTL without a name (Timestamp_1). Dropping
        // only "ttl_timestamp" left that index in place; the next CreateMany
        // then 500'd with IndexOptionsConflict and the button died.
        await DropTtlIndexesAsync(col, ct);
        await EnsureIndexesAsync(col, days, ct);
        Invalidate(slug);
    }

    private IMongoCollection<BsonDocument> Collection(string slug) =>
        _client!.GetDatabase(TenantLogNames.Database(slug))
            .GetCollection<BsonDocument>(TenantLogNames.Collection);

    private async Task<LogTenantRowDto> ReadRowAsync(Tenant tenant, CancellationToken ct)
    {
        try
        {
            var db = _client!.GetDatabase(TenantLogNames.Database(tenant.Slug));
            var col = db.GetCollection<BsonDocument>(TenantLogNames.Collection);
            BsonDocument stats;
            try
            {
                stats = await db.RunCommandAsync<BsonDocument>(
                    new BsonDocument { ["collStats"] = TenantLogNames.Collection }, cancellationToken: ct);
            }
            catch (MongoCommandException)
            {
                return new LogTenantRowDto(
                    tenant.Slug, tenant.Id, tenant.Status.ToString(),
                    DatabaseExists: true, 0, 0, null, null, null, tenant.Status == TenantStatus.Active);
            }

            DateTime? oldest = null, newest = null;
            var newestDoc = await col.Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Descending("Timestamp"))
                .Limit(1).FirstOrDefaultAsync(ct);
            var oldestDoc = await col.Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("Timestamp"))
                .Limit(1).FirstOrDefaultAsync(ct);
            if (newestDoc is not null) newest = ReadTime(newestDoc);
            if (oldestDoc is not null) oldest = ReadTime(oldestDoc);

            var ttl = await ReadTtlDaysAsync(col, ct);
            var silent = tenant.Status == TenantStatus.Active
                         && (newest is null || newest < DateTime.UtcNow.AddHours(-_settings.SilentAfterHours));

            return new LogTenantRowDto(
                tenant.Slug,
                tenant.Id,
                tenant.Status.ToString(),
                DatabaseExists: true,
                SizeBytes: stats.GetValue("size", 0).ToInt64(),
                Documents: stats.GetValue("count", 0).ToInt64(),
                oldest,
                newest,
                ttl,
                silent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read journal stats for {Slug}", tenant.Slug);
            return MissingRow(tenant);
        }
    }

    private static LogTenantRowDto MissingRow(Tenant tenant) =>
        new(tenant.Slug, tenant.Id, tenant.Status.ToString(), false, null, null, null, null, null, false);

    private void Invalidate(string slug) => _cache.Remove($"log-stats:{slug}");

    private static async Task<int?> ReadTtlDaysAsync(IMongoCollection<BsonDocument> col, CancellationToken ct)
    {
        using var cursor = await col.Indexes.ListAsync(ct);
        var indexes = await cursor.ToListAsync(ct);
        foreach (var idx in indexes)
        {
            if (!idx.Contains("expireAfterSeconds")) continue;
            return (int)Math.Round(idx["expireAfterSeconds"].ToDouble() / 86400.0);
        }
        return null;
    }

    internal static async Task DropTtlIndexesAsync(
        IMongoCollection<BsonDocument> col, CancellationToken ct)
    {
        using var cursor = await col.Indexes.ListAsync(ct);
        foreach (var idx in await cursor.ToListAsync(ct))
        {
            if (!idx.Contains("expireAfterSeconds")) continue;
            var name = idx.GetValue("name", "").AsString;
            if (string.IsNullOrEmpty(name) || name == "_id_") continue;
            try { await col.Indexes.DropOneAsync(name, ct); }
            catch (MongoCommandException) { /* already gone */ }
        }
    }

    internal static async Task EnsureIndexesAsync(
        IMongoCollection<BsonDocument> col, int ttlDays, CancellationToken ct)
    {
        var keys = Builders<BsonDocument>.IndexKeys;
        await col.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(keys.Descending("Timestamp")),
            new CreateIndexModel<BsonDocument>(keys.Ascending("Level").Descending("Timestamp")),
            new CreateIndexModel<BsonDocument>(keys.Ascending("RequestId")),
            new CreateIndexModel<BsonDocument>(keys.Ascending("JobId")),
            new CreateIndexModel<BsonDocument>(
                keys.Ascending("Timestamp"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(ttlDays) }),
        ], ct);
    }

    private static FilterDefinition<BsonDocument> BuildFilter(
        DateTime fromUtc,
        DateTime toUtc,
        string? minLevel,
        string? sourceContains,
        string? channel,
        string? text,
        string? userName,
        string? requestId,
        Guid? jobId,
        Guid? agentId,
        DateTime? cursorTimestamp,
        string? cursorId)
    {
        var f = Builders<BsonDocument>.Filter;
        var parts = new List<FilterDefinition<BsonDocument>>
        {
            f.Gte("Timestamp", fromUtc),
            f.Lte("Timestamp", toUtc),
        };

        if (!string.IsNullOrWhiteSpace(minLevel))
        {
            var min = Array.IndexOf(LevelOrder, minLevel);
            if (min >= 0)
                parts.Add(f.In("Level", LevelOrder.Skip(min)));
        }

        if (!string.IsNullOrWhiteSpace(sourceContains))
            parts.Add(f.Regex("SourceContext", new BsonRegularExpression(Regex.Escape(sourceContains), "i")));

        if (!string.IsNullOrWhiteSpace(channel))
            parts.Add(f.Eq("Channel", channel));

        if (!string.IsNullOrWhiteSpace(text))
        {
            var rx = new BsonRegularExpression(Regex.Escape(text), "i");
            parts.Add(f.Or(f.Regex("Message", rx), f.Regex("Exception", rx)));
        }

        if (!string.IsNullOrWhiteSpace(userName))
            parts.Add(f.Eq("UserName", userName));
        if (!string.IsNullOrWhiteSpace(requestId))
            parts.Add(f.Eq("RequestId", requestId));
        if (jobId is { } j)
            parts.Add(f.Eq("JobId", j.ToString()));
        if (agentId is { } a)
            parts.Add(f.Eq("AgentId", a.ToString()));

        if (cursorTimestamp is { } cur && !string.IsNullOrWhiteSpace(cursorId))
        {
            parts.Add(f.Or(
                f.Lt("Timestamp", cur),
                f.And(f.Eq("Timestamp", cur), f.Lt("_id", cursorId))));
        }

        return f.And(parts);
    }

    private static LogEventDto ToEvent(BsonDocument d, string slug)
    {
        var props = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (d.TryGetValue("Properties", out var raw) && raw.IsBsonDocument)
        {
            foreach (var el in raw.AsBsonDocument)
                props[el.Name] = Scalar(el.Value);
        }

        return new LogEventDto(
            Id: d.GetValue("_id", "").ToString() ?? "",
            Slug: slug,
            Timestamp: ReadTime(d) ?? DateTime.UnixEpoch,
            Level: d.GetValue("Level", "Information").ToString() ?? "Information",
            Message: d.GetValue("Message", "").ToString() ?? "",
            MessageTemplate: Opt(d, "MessageTemplate"),
            Exception: Opt(d, "Exception"),
            SourceContext: Opt(d, "SourceContext"),
            Application: Opt(d, "Application"),
            Version: Opt(d, "Version"),
            Instance: Opt(d, "Instance"),
            TenantSlug: Opt(d, "TenantSlug") ?? slug,
            UserId: Opt(d, "UserId"),
            UserName: Opt(d, "UserName") ?? Opt(d, "User"),
            RequestId: Opt(d, "RequestId"),
            JobId: Guid.TryParse(Opt(d, "JobId"), out var job) ? job : null,
            JobName: Opt(d, "JobName"),
            AgentId: Guid.TryParse(Opt(d, "AgentId"), out var agent) ? agent : null,
            AgentTag: Opt(d, "AgentTag"),
            Channel: Opt(d, "Channel") ?? "System",
            Properties: props);
    }

    private static DateTime? ReadTime(BsonDocument d)
    {
        if (d.TryGetValue("Timestamp", out var t) && t.IsValidDateTime)
            return t.ToUniversalTime();
        if (d.TryGetValue("UtcTimestamp", out var u) && u.IsValidDateTime)
            return u.ToUniversalTime();
        return null;
    }

    private static string? Opt(BsonDocument d, string name) =>
        d.TryGetValue(name, out var v) && v.BsonType is not BsonType.Null ? Scalar(v) : null;

    private static string? Scalar(BsonValue v) => v.BsonType switch
    {
        BsonType.Null => null,
        BsonType.String => v.AsString,
        BsonType.Boolean => v.AsBoolean ? "true" : "false",
        BsonType.DateTime => v.ToUniversalTime().ToString("o"),
        BsonType.ObjectId => v.AsObjectId.ToString(),
        _ => v.ToString(),
    };
}

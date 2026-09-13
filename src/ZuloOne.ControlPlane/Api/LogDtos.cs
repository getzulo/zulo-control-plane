namespace ZuloOne.ControlPlane.Api;

public sealed record LogStoreStatusDto(
    string State,
    string? Error,
    long? ClusterSizeBytes,
    int TenantCount,
    int MissingDatabaseCount);

public sealed record LogTenantRowDto(
    string Slug,
    Guid TenantId,
    string Status,
    bool DatabaseExists,
    long? SizeBytes,
    long? Documents,
    DateTime? OldestUtc,
    DateTime? NewestUtc,
    int? TtlDays,
    bool SinkSilent);

public sealed record LogEventDto(
    string Id,
    string Slug,
    DateTime Timestamp,
    string Level,
    string Message,
    string? MessageTemplate,
    string? Exception,
    string? SourceContext,
    string? Application,
    string? Version,
    string? Instance,
    string? TenantSlug,
    string? UserId,
    string? UserName,
    string? RequestId,
    Guid? JobId,
    string? JobName,
    Guid? AgentId,
    string? AgentTag,
    string Channel,
    IReadOnlyDictionary<string, string?> Properties);

public sealed record LogEventsResponse(IReadOnlyList<LogEventDto> Items, int Take);

public sealed record LogPurgeRequest(int? OlderThanDays, bool All, string ConfirmSlug);

public sealed record LogTtlRequest(int Days);

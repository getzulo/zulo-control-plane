using Npgsql;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>One model as the tenant's own database has it.</summary>
public sealed record InstalledModel(
    Guid MetaId,
    string Name,
    string? Version,
    string? Publisher,
    bool IsSystem,
    bool IsEnabled,
    string? CompilationStatus,
    string? CompilationError);

/// <summary>
/// What business layer a tenant actually has, read from the tenant itself.
/// </summary>
/// <remarks>
/// <para>
/// The registry records which image a tenant is pinned to; it has never recorded
/// what that image installed. Those are different facts, and the gap between them
/// is where the interesting failures live — a tenant on the right image whose
/// models did not compile, or one provisioned before the packages existed and
/// therefore carrying nothing at all. Both look identical from the fleet list.
/// </para>
///
/// <para>
/// Read on demand, from the database, rather than reported by the tenant. The
/// panel cannot call into a tenant's HTTP — the socket proxy forbids exec and
/// there is no route in — but it already holds admin credentials for every tenant
/// database, which is how stats work too.
/// </para>
/// </remarks>
public sealed class TenantModelsService
{
    private readonly TenantDatabaseSettings _db;
    private readonly ILogger<TenantModelsService> _logger;

    public TenantModelsService(
        Microsoft.Extensions.Options.IOptions<TenantDatabaseSettings> db,
        ILogger<TenantModelsService> logger)
    {
        _db = db.Value;
        _logger = logger;
    }

    public async Task<(List<InstalledModel> Models, string? Error)> ReadAsync(Tenant tenant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
            return ([], "The registry holds no database name for this tenant.");

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString(tenant));
            await connection.OpenAsync(ct);

            // A tenant provisioned before the platform seeded Core has no table at
            // all. That is a real state — it is what every tenant looked like
            // before ModelSeeder — and it must read as "nothing installed" rather
            // than as an error.
            await using (var exists = new NpgsqlCommand(
                "SELECT to_regclass('public.\"MetaModels\"') IS NOT NULL", connection))
            {
                if (await exists.ExecuteScalarAsync(ct) is not true)
                    return ([], null);
            }

            const string sql = """
                SELECT "MetaId", "Name", "ModelVersion", "Publisher", "IsSystem", "IsEnabled",
                       "CompilationStatus", "CompilationError"
                  FROM "MetaModels"
                 ORDER BY "IsSystem" DESC, "Name"
                """;

            var models = new List<InstalledModel>();
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                models.Add(new InstalledModel(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    !reader.IsDBNull(4) && reader.GetBoolean(4),
                    !reader.IsDBNull(5) && reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            return (models, null);
        }
        catch (Exception ex)
        {
            // Returned rather than thrown: the tenant page shows containers,
            // databases and snapshots too, and one unreachable database should
            // cost that section, not the page.
            _logger.LogWarning(ex, "Could not read the models of {Slug}.", tenant.Slug);
            return ([], ex.Message);
        }
    }

    /// <summary>
    /// Names of models that declare a direct dependency on <paramref name="modelId"/>.
    /// Same grain as Core's 409.
    /// </summary>
    public async Task<(List<string> Names, string? Error)> ReadDependentsAsync(
        Tenant tenant, Guid modelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
            return ([], "The registry holds no database name for this tenant.");

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString(tenant));
            await connection.OpenAsync(ct);
            await using (var exists = new NpgsqlCommand(
                "SELECT to_regclass('public.\"MetaModelDependencies\"') IS NOT NULL", connection))
            {
                if (await exists.ExecuteScalarAsync(ct) is not true)
                    return ([], null);
            }

            const string sql = """
                SELECT m."Name"
                  FROM "MetaModelDependencies" d
                  JOIN "MetaModels" m ON m."MetaId" = d."ModelMetaId"
                 WHERE d."DependsOnModelMetaId" = @id
                   AND d."ModelMetaId" <> @id
                 ORDER BY m."Name"
                """;
            var names = new List<string>();
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", modelId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                names.Add(reader.GetString(0));
            return (names, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read dependents of {Model} on {Slug}.", modelId, tenant.Slug);
            return ([], ex.Message);
        }
    }

    private string ConnectionString(Tenant tenant)
    {
        var ssl = _db.RequireSsl ? "SSL Mode=Require;Trust Server Certificate=true;" : string.Empty;
        var target = _db.Host.Contains(',') ? "Target Session Attributes=Primary;" : string.Empty;
        return $"Host={_db.Host};Port={_db.Port};Database={tenant.DatabaseName};" +
               $"Username={_db.AdminUser};Password={_db.AdminPassword};{ssl}Pooling=false;{target}";
    }
}

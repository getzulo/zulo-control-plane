using Npgsql;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>One model as the tenant's own database has it.</summary>
public sealed record InstalledModel(
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

        var ssl = _db.RequireSsl ? "SSL Mode=Require;Trust Server Certificate=true;" : string.Empty;
        var target = _db.Host.Contains(',') ? "Target Session Attributes=Primary;" : string.Empty;
        var connectionString =
            $"Host={_db.Host};Port={_db.Port};Database={tenant.DatabaseName};" +
            $"Username={_db.AdminUser};Password={_db.AdminPassword};{ssl}Pooling=false;{target}";

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
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
                SELECT "Name", "ModelVersion", "Publisher", "IsSystem", "IsEnabled",
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
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    !reader.IsDBNull(3) && reader.GetBoolean(3),
                    !reader.IsDBNull(4) && reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
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
}

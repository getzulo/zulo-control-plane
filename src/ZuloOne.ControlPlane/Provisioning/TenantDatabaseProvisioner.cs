using Microsoft.Extensions.Options;
using Npgsql;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Creates and drops the per-tenant database and its owner role on the managed
/// Postgres (ControlPlane.Deployment.md §6 step 2). One database per tenant is
/// the data-isolation boundary: a tenant's role owns its own database and nothing
/// else, so one customer can never read another's rows.
/// </summary>
public sealed class TenantDatabaseProvisioner
{
    private readonly TenantDatabaseSettings _settings;
    private readonly ILogger<TenantDatabaseProvisioner> _logger;

    public TenantDatabaseProvisioner(IOptions<TenantDatabaseSettings> settings, ILogger<TenantDatabaseProvisioner> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Creates role + database for the slug and returns the connection the tenant will use.</summary>
    public async Task<(string Database, string Role, string Password, string ConnectionString)> CreateAsync(
        string slug, CancellationToken ct = default)
    {
        var database = $"tenant_{slug}";
        var role = $"tenant_{slug}";
        var password = GeneratePassword();

        await using var admin = new NpgsqlConnection(AdminConnectionString());
        await admin.OpenAsync(ct);

        // Identifiers cannot be parameterised, so they are quoted and the slug is
        // validated up front (see TenantProvisioner.ValidateSlug) — a slug is a DNS
        // label, which excludes everything that could break out of the quotes.
        await ExecuteAsync(admin, $"CREATE ROLE \"{role}\" WITH LOGIN PASSWORD '{password.Replace("'", "''")}'", ct);
        await ExecuteAsync(admin, $"CREATE DATABASE \"{database}\" OWNER \"{role}\"", ct);

        // PG15+ revoked CREATE on public from non-owners; without this the tenant's
        // first boot cannot create its own tables.
        await using var inTenant = new NpgsqlConnection(AdminConnectionString(database));
        await inTenant.OpenAsync(ct);
        await ExecuteAsync(inTenant, $"GRANT ALL ON SCHEMA public TO \"{role}\"", ct);

        _logger.LogInformation("Created database {Database} owned by {Role}", database, role);
        return (database, role, password, TenantConnectionString(database, role, password));
    }

    /// <summary>Drops the tenant's database and role. Best-effort: a half-deleted
    /// tenant must not block the rest of the teardown.</summary>
    public async Task DropAsync(string? database, string? role, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(database) && string.IsNullOrWhiteSpace(role)) return;

        await using var admin = new NpgsqlConnection(AdminConnectionString());
        await admin.OpenAsync(ct);

        if (!string.IsNullOrWhiteSpace(database))
        {
            // Sessions still attached would make DROP DATABASE fail.
            await TryAsync(admin, $"""
                SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database.Replace("'", "''")}'
                """, ct);
            await TryAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\"", ct);
        }
        if (!string.IsNullOrWhiteSpace(role))
        {
            await TryAsync(admin, $"DROP ROLE IF EXISTS \"{role}\"", ct);
        }
    }

    public string TenantConnectionString(string database, string role, string password)
    {
        var host = string.IsNullOrWhiteSpace(_settings.TenantHost) ? _settings.Host : _settings.TenantHost;
        var ssl = _settings.RequireSsl ? "SSL Mode=Require;Trust Server Certificate=true;" : string.Empty;
        return $"Host={host};Port={_settings.Port};Database={database};Username={role};Password={password};{ssl}";
    }

    private string AdminConnectionString(string? database = null)
    {
        var ssl = _settings.RequireSsl ? "SSL Mode=Require;Trust Server Certificate=true;" : string.Empty;
        return $"Host={_settings.Host};Port={_settings.Port};Database={database ?? _settings.AdminDatabase};"
             + $"Username={_settings.AdminUser};Password={_settings.AdminPassword};{ssl}";
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task TryAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        try { await ExecuteAsync(connection, sql, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Teardown statement failed (continuing): {Sql}", sql); }
    }

    /// <summary>URL-safe so it survives a connection string without escaping.</summary>
    private static string GeneratePassword()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

using Microsoft.Extensions.Options;
using Npgsql;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Operations against a tenant's own application data, as opposed to its
/// container or its database container.
///
/// <para>
/// Reaches the tenant's database DIRECTLY rather than through its API, and that is
/// deliberate: the case this exists for is "nobody can sign in any more", so an
/// endpoint that requires signing in is no use. The panel already holds the
/// tenant's database credentials and can drop the whole database, so this adds no
/// authority it did not have — only a smaller, named way to use it.
/// </para>
/// </summary>
public sealed class TenantAdminService
{
    private readonly TenantDatabaseSettings _settings;
    private readonly ILogger<TenantAdminService> _logger;

    public TenantAdminService(IOptions<TenantDatabaseSettings> settings, ILogger<TenantAdminService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Sets a new password for one of the tenant's users and returns it once.
    ///
    /// Also clears <c>IsLocked</c> — a reset exists to restore access, and handing
    /// back a password for an account that still refuses it would be a reset in
    /// name only. <c>MustChangePassword</c> is set for the opposite reason: an
    /// operator has now seen this password, so the user should replace it.
    /// </summary>
    /// <param name="userName">
    /// Which user. Defaults to the tenant's recorded administrator, falling back to
    /// the account named <c>admin</c> that provisioning seeds.
    /// </param>
    public async Task<(bool Found, string Password, string User)> ResetPasswordAsync(
        Tenant tenant, string? userName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
            throw new InvalidOperationException($"'{tenant.Slug}' has no database.");

        var password = GeneratePassword();
        // Work factor left at the library default, which is what the tenant's own
        // AuthController uses — a hash this side must be verifiable by that side.
        var hash = BCrypt.Net.BCrypt.HashPassword(password);

        await using var connection = new NpgsqlConnection(ConnectionString(tenant.DatabaseName!));
        await connection.OpenAsync(ct);

        // Match on name OR e-mail so either identifies the account, and prefer an
        // exact name. Parameterised: this is the one place the panel writes into a
        // tenant's own data, and the value comes from an operator's text box.
        const string sql = """
            UPDATE "MetaUsers"
               SET "PasswordHash" = @hash,
                   "IsLocked" = false,
                   "MustChangePassword" = true,
                   "LastPasswordChange" = now() AT TIME ZONE 'utc',
                   "ModifiedDateTime" = now() AT TIME ZONE 'utc'
             WHERE "MetaId" = (
                   SELECT "MetaId" FROM "MetaUsers"
                    WHERE "Name" = @who OR "Email" = @who
                    ORDER BY CASE WHEN "Name" = @who THEN 0 ELSE 1 END
                    LIMIT 1)
            RETURNING "Name";
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("hash", hash);
        cmd.Parameters.AddWithValue("who", userName?.Trim() is { Length: > 0 } n ? n : (tenant.AdminEmail ?? "admin"));

        var name = await cmd.ExecuteScalarAsync(ct) as string;
        if (name is null)
        {
            // Try the seeded account before giving up: a tenant whose AdminEmail was
            // recorded but whose user was created under a different address is
            // exactly the situation somebody is trying to recover from.
            await using var fallback = new NpgsqlCommand(sql, connection);
            fallback.Parameters.AddWithValue("hash", hash);
            fallback.Parameters.AddWithValue("who", "admin");
            name = await fallback.ExecuteScalarAsync(ct) as string;
        }

        if (name is null) return (false, string.Empty, string.Empty);

        _logger.LogWarning("Administrator password for tenant {Slug} was reset for user {User}", tenant.Slug, name);
        return (true, password, name);
    }

    /// <summary>Who the tenant has, so an operator can pick before resetting.</summary>
    public async Task<IReadOnlyList<(string Name, string? Email, bool Active, bool Locked)>> ListUsersAsync(
        Tenant tenant, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName)) return [];

        await using var connection = new NpgsqlConnection(ConnectionString(tenant.DatabaseName!));
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """SELECT "Name", "Email", "IsActive", "IsLocked" FROM "MetaUsers" ORDER BY "Name" LIMIT 200""", connection);

        var rows = new List<(string, string?, bool, bool)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        return rows;
    }

    private string ConnectionString(string database)
    {
        var ssl = _settings.RequireSsl ? "SSL Mode=Require;Trust Server Certificate=true;" : string.Empty;
        var target = _settings.Host.Contains(',') ? "Target Session Attributes=Primary;" : string.Empty;
        // Pooling off, like the provisioner's admin connections: these are rare
        // administrative touches, and a pooled handle can be killed out from under
        // the pool by a pg_terminate_backend elsewhere.
        return $"Host={_settings.Host};Port={_settings.Port};Database={database};"
             + $"Username={_settings.AdminUser};Password={_settings.AdminPassword};{ssl}Pooling=false;{target}";
    }

    /// <summary>Readable aloud and typeable: this gets read off a screen once.</summary>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}

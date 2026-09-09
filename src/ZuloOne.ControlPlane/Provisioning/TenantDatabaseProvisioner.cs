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

        // PostgreSQL 16 stopped letting a CREATEROLE role implicitly act as the
        // roles it creates. Without this the statement AFTER the next one fails:
        //
        //   42501: must be able to SET ROLE "tenant_<slug>"
        //
        // because CREATE DATABASE ... OWNER requires the creator to be able to
        // become that owner. This GUC makes each CREATE ROLE grant membership back
        // to the creator with SET — exactly the capability the next line needs, and
        // nothing beyond it.
        //
        // A no-op when the admin connection is a superuser, so it is safe either
        // way, and far preferable to making the control plane a superuser: that
        // would let a compromise read every tenant's data rather than only create
        // databases.
        await ExecuteAsync(admin, "SET createrole_self_grant = 'set, inherit'", ct);

        // Identifiers cannot be parameterised, so they are quoted and the slug is
        // validated up front (see TenantProvisioner.ValidateSlug) — a slug is a DNS
        // label, which excludes everything that could break out of the quotes.
        //
        // Cleans up after ITSELF on failure. The caller cannot: it learns the role
        // and database names only from this method's return value, so anything that
        // throws partway leaves artifacts the caller has no record of and cannot
        // roll back. Observed exactly that — CREATE ROLE succeeded, CREATE DATABASE
        // failed, and the orphaned role then made every retry of the same slug fail
        // with 42710 "role already exists", which reads as a duplicate-tenant error
        // rather than debris from the previous attempt.
        var roleCreated = false;
        var databaseCreated = false;
        try
        {
            await ExecuteAsync(admin, $"CREATE ROLE \"{role}\" WITH LOGIN PASSWORD '{password.Replace("'", "''")}'", ct);
            roleCreated = true;
            await ExecuteAsync(admin, $"CREATE DATABASE \"{database}\" OWNER \"{role}\"", ct);
            databaseCreated = true;

            var adminUser = _settings.AdminUser.Replace("\"", "\"\"");

            // Membership stated OUTRIGHT rather than inherited from the GUC above.
            // createrole_self_grant already grants it as a side effect, which is
            // precisely the problem: it only fires for roles the control plane
            // created itself. The first tenant was made by hand, so the panel could
            // read 0 of its 109 tables — and pg_dump against it produces an EMPTY
            // backup rather than an error. A backup feature resting on a side effect
            // passes acceptance on a provisioned tenant and fails silently on the
            // one that was not.
            await ExecuteAsync(admin, $"GRANT \"{role}\" TO \"{adminUser}\"", ct);

            // PUBLIC keeps CONNECT on a new database by default, so without this
            // every tenant role can open a session against every OTHER tenant's
            // database, and against the registry — which stores each tenant's
            // database password and JWT signing key in plaintext. Table grants stop
            // it reading rows, but the catalogue stays enumerable, and ZuloOne runs
            // tenant-authored scripts.
            //
            // The owner keeps CONNECT through ownership, so the tenant itself is
            // unaffected; only the panel needs it restored by name.
            await ExecuteAsync(admin, $"REVOKE CONNECT ON DATABASE \"{database}\" FROM PUBLIC", ct);
            await ExecuteAsync(admin, $"GRANT CONNECT ON DATABASE \"{database}\" TO \"{adminUser}\"", ct);
        }
        catch
        {
            // CancellationToken.None throughout: the cleanup must not be skipped
            // because the thing that failed was a cancellation.
            if (databaseCreated)
            {
                try { await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\"", CancellationToken.None); }
                catch (Exception cleanup)
                {
                    _logger.LogError(cleanup,
                        "Could not drop database {Database} after a failed create — a retry of this slug will report that it already exists",
                        database);
                }
            }
            if (roleCreated)
            {
                try { await ExecuteAsync(admin, $"DROP ROLE IF EXISTS \"{role}\"", CancellationToken.None); }
                catch (Exception cleanup)
                {
                    _logger.LogError(cleanup,
                        "Could not drop role {Role} after a failed create — a retry of this slug will report that the role already exists",
                        role);
                }
            }
            throw;
        }

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

    /// <summary>
    /// Puts a restored database in place of a live one, keeping the live tenant's
    /// identity — same name, same role, same password, so its container's connection
    /// string does not change and its running configuration is untouched.
    ///
    /// <para>
    /// The displaced database is RENAMED rather than dropped, and that rename is the
    /// undo. It is instant and lossless, which a fresh dump at this moment would not
    /// be; the operator discards it explicitly once satisfied.
    /// </para>
    ///
    /// <para>
    /// Callers MUST stop both containers first. Two things follow from
    /// <c>ALTER DATABASE … RENAME</c>: it needs the database to have no sessions, and
    /// it cannot run inside a transaction — so there is a brief window between the
    /// two renames where the live name does not exist. If the second fails, the
    /// recovery is one statement, and it is named in the exception rather than left
    /// to be worked out.
    /// </para>
    /// </summary>
    public async Task SwapAsync(
        string liveDatabase, string liveRole, string scratchDatabase, string scratchRole,
        string archiveDatabase, CancellationToken ct = default)
    {
        await using var admin = new NpgsqlConnection(AdminConnectionString());
        await admin.OpenAsync(ct);

        // Both, and not only the live one: the scratch copy's own container was just
        // stopped, and a lingering pooled session blocks its rename too.
        foreach (var database in new[] { liveDatabase, scratchDatabase })
        {
            await ExecuteAsync(admin,
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database.Replace("'", "''")}'", ct);
        }

        await ExecuteAsync(admin, $"ALTER DATABASE \"{liveDatabase}\" RENAME TO \"{archiveDatabase}\"", ct);
        try
        {
            await ExecuteAsync(admin, $"ALTER DATABASE \"{scratchDatabase}\" RENAME TO \"{liveDatabase}\"", ct);
        }
        catch (Exception ex)
        {
            // Put it back rather than leave the tenant with no database at all.
            try
            {
                await ExecuteAsync(admin,
                    $"ALTER DATABASE \"{archiveDatabase}\" RENAME TO \"{liveDatabase}\"", CancellationToken.None);
                throw new InvalidOperationException(
                    $"The swap failed and was undone — '{liveDatabase}' is back as it was. Cause: {ex.Message}", ex);
            }
            catch (Exception undo) when (undo is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"The swap failed AND could not be undone. '{liveDatabase}' currently does not exist; " +
                    $"it is present as '{archiveDatabase}'. Recover with: " +
                    $"ALTER DATABASE \"{archiveDatabase}\" RENAME TO \"{liveDatabase}\"; — original cause: {ex.Message}", ex);
            }
        }

        // The database now carries the live name but is still owned by the scratch
        // role, and every object inside it with it. Both must move, or the live
        // tenant cannot alter its own tables and dropping the scratch role fails.
        await ExecuteAsync(admin, $"ALTER DATABASE \"{liveDatabase}\" OWNER TO \"{liveRole}\"", ct);

        await using var inTenant = new NpgsqlConnection(AdminConnectionString(liveDatabase));
        await inTenant.OpenAsync(ct);
        await ExecuteAsync(inTenant, $"REASSIGN OWNED BY \"{scratchRole}\" TO \"{liveRole}\"", ct);
        // Whatever REASSIGN does not move is a privilege rather than an object;
        // without dropping those the role cannot be removed afterwards.
        await ExecuteAsync(inTenant, $"DROP OWNED BY \"{scratchRole}\"", ct);
        await ExecuteAsync(inTenant, $"GRANT ALL ON SCHEMA public TO \"{liveRole}\"", ct);
    }

    /// <summary>Drops one database outright — the explicit discard of a kept copy.</summary>
    public async Task DropDatabaseAsync(string database, CancellationToken ct = default)
    {
        await using var admin = new NpgsqlConnection(AdminConnectionString());
        await admin.OpenAsync(ct);
        await ExecuteAsync(admin,
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database.Replace("'", "''")}'", ct);
        await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\"", ct);
    }

    public string TenantConnectionString(string database, string role, string password)
    {
        var host = string.IsNullOrWhiteSpace(_settings.TenantHost) ? _settings.Host : _settings.TenantHost;
        var ssl = _settings.RequireSsl ? "SSL Mode=Require;Trust Server Certificate=true;" : string.Empty;
        return $"Host={host};Port={_settings.Port};Database={database};Username={role};Password={password};{ssl}"
             + TargetPrimary(host);
    }

    private string AdminConnectionString(string? database = null)
    {
        var ssl = _settings.RequireSsl ? "SSL Mode=Require;Trust Server Certificate=true;" : string.Empty;
        return $"Host={_settings.Host};Port={_settings.Port};Database={database ?? _settings.AdminDatabase};"
             + $"Username={_settings.AdminUser};Password={_settings.AdminPassword};{ssl}"
             + TargetPrimary(_settings.Host);
    }

    /// <summary>
    /// Pins a multi-host connection to the writable node.
    /// </summary>
    /// <remarks>
    /// A replicated cluster is addressed by listing every node — Npgsql then probes
    /// <c>pg_is_in_recovery()</c> and follows a failover on its own. Without
    /// <c>Target Session Attributes=Primary</c> it simply takes whichever node answers
    /// first, which may be a hot standby: every write then fails with
    /// <c>cannot execute INSERT in a read-only transaction</c>, intermittently and
    /// only under load, which is a genuinely unpleasant thing to diagnose.
    ///
    /// Both callers need it. The admin connection issues CREATE DATABASE / CREATE ROLE,
    /// and the tenant connection runs EF migrations and generates DDL on every boot.
    ///
    /// Only emitted for a host LIST: on a single host the parameter is redundant, and
    /// adding it would make a standalone standby unusable for read-only work.
    /// </remarks>
    private static string TargetPrimary(string host) =>
        host.Contains(',') ? "Target Session Attributes=Primary;" : string.Empty;

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

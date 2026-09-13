using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>Credentials for one tenant's isolated Mongo journal database.</summary>
public sealed record TenantLogDatabase(
    string Database,
    string User,
    string Password,
    string ConnectionUrl);

/// <summary>
/// Creates and drops the per-tenant database user and journal collection on the
/// farm Mongo. An empty operator URL disables provisioning for local deployments.
/// </summary>
public sealed class TenantLogDatabaseProvisioner
{
    private readonly TenantLogSettings _settings;
    private readonly IMongoClient? _client;
    private readonly ILogger<TenantLogDatabaseProvisioner> _logger;

    public TenantLogDatabaseProvisioner(
        IOptions<TenantLogSettings> settings,
        ILogger<TenantLogDatabaseProvisioner> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(_settings.Url))
            _client = new MongoClient(_settings.Url);
    }

    public bool IsConfigured => _client is not null;

    /// <summary>
    /// Creates a database-scoped read/write user and the indexed events collection.
    /// Returns null when Mongo logging is not configured.
    /// </summary>
    public async Task<TenantLogDatabase?> CreateAsync(string slug, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var databaseName = TenantLogNames.Database(slug);
        var user = TenantLogNames.User(slug);
        var password = GeneratePassword();
        var database = _client.GetDatabase(databaseName);
        var userCreated = false;

        try
        {
            await database.RunCommandAsync<BsonDocument>(
                new BsonDocument
                {
                    ["createUser"] = user,
                    ["pwd"] = password,
                    ["roles"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["role"] = "readWrite",
                            ["db"] = databaseName,
                        },
                    },
                },
                cancellationToken: ct);
            userCreated = true;

            await database.CreateCollectionAsync(TenantLogNames.Collection, cancellationToken: ct);
            var collection = database.GetCollection<BsonDocument>(TenantLogNames.Collection);
            await TenantLogStore.EnsureIndexesAsync(collection, _settings.TtlDays, ct);
        }
        catch
        {
            if (userCreated)
            {
                await TryAsync(
                    () => database.RunCommandAsync<BsonDocument>(
                        new BsonDocument("dropUser", user),
                        cancellationToken: CancellationToken.None),
                    "drop user {User} after a failed create",
                    user);
                await TryAsync(
                    () => _client.DropDatabaseAsync(databaseName, CancellationToken.None),
                    "drop database {Database} after a failed create",
                    databaseName);
            }
            throw;
        }

        _logger.LogInformation(
            "Created Mongo journal database {Database} for user {User}",
            databaseName,
            user);
        return new TenantLogDatabase(
            databaseName,
            user,
            password,
            TenantConnectionUrl(databaseName, user, password));
    }

    /// <summary>Builds the URL given to a tenant container, preserving cluster options.</summary>
    public string TenantConnectionUrl(string database, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(_settings.Url))
            throw new InvalidOperationException("TenantLogs:Url is not configured.");

        var builder = new MongoUrlBuilder(_settings.Url)
        {
            Username = user,
            Password = password,
            DatabaseName = database,
            AuthenticationSource = database,
        };
        return builder.ToString();
    }

    /// <summary>Best-effort removal; a half-deleted journal must not block teardown.</summary>
    public async Task DropAsync(string? databaseName, string? user, CancellationToken ct = default)
    {
        if (_client is null || string.IsNullOrWhiteSpace(databaseName)) return;

        var database = _client.GetDatabase(databaseName);
        if (!string.IsNullOrWhiteSpace(user))
        {
            await TryAsync(
                () => database.RunCommandAsync<BsonDocument>(
                    new BsonDocument("dropUser", user),
                    cancellationToken: ct),
                "drop user {User}",
                user);
        }

        await TryAsync(
            () => _client.DropDatabaseAsync(databaseName, ct),
            "drop database {Database}",
            databaseName);
    }

    private async Task TryAsync(Func<Task> action, string message, string value)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Could not {message} (continuing)", value);
        }
    }

    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
}

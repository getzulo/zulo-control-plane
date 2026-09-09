using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Settings;

/// <summary>
/// The value in force for a configuration key, and where it came from.
/// </summary>
/// <remarks>
/// Three layers, highest first: the <see cref="Setting"/> table, then
/// configuration (appsettings, then <c>cp.env</c>), then the compiled-in default
/// declared in <see cref="SettingsCatalog"/>.
///
/// <para>
/// A singleton holding the database layer in memory. Reads are synchronous and
/// free, which matters because they happen on the provisioning path; the cache is
/// invalidated on write rather than on a timer, since the only writer is
/// <c>SettingsController</c> and it is right here.
/// </para>
///
/// <para>
/// Configuration is NOT copied into the cache. It is read live through
/// <see cref="IConfiguration"/>, so the two layers cannot drift and an operator
/// removing an override immediately sees what <c>cp.env</c> actually says.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<SettingsStore> _logger;

    // Replaced wholesale on reload rather than mutated, so a reader mid-request
    // sees one consistent generation and never a half-applied set.
    private volatile Dictionary<string, string> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    public SettingsStore(IServiceScopeFactory scopes, IConfiguration config, ILogger<SettingsStore> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    /// <summary>Re-read the override table. Called at startup and after every write.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rows = await db.Settings.AsNoTracking().ToListAsync(ct);
        _overrides = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation("Settings: {Count} override(s) in force.", _overrides.Count);
    }

    /// <summary>The raw override, or null when the key falls through to configuration.</summary>
    public string? Override(string key) => _overrides.GetValueOrDefault(key);

    /// <summary>Configuration's answer, ignoring both the override and the default.</summary>
    public string? FromConfig(string key) => _config[key];

    /// <summary>Where the value in force came from.</summary>
    public SettingSource SourceOf(string key)
    {
        if (_overrides.ContainsKey(key)) return SettingSource.Database;
        return string.IsNullOrWhiteSpace(_config[key]) ? SettingSource.Default : SettingSource.Config;
    }

    /// <summary>The value in force, as a string.</summary>
    public string Raw(string key)
    {
        if (_overrides.TryGetValue(key, out var v)) return v;
        var fromConfig = _config[key];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
        return SettingsCatalog.Find(key)?.Default ?? string.Empty;
    }

    public int Int(string key)
        => int.TryParse(Raw(key), out var v) ? v : Fallback(key, int.Parse);

    public long Long(string key)
        => long.TryParse(Raw(key), out var v) ? v : Fallback(key, long.Parse);

    public bool Bool(string key)
        => bool.TryParse(Raw(key), out var v) ? v : Fallback(key, bool.Parse);

    public string Text(string key) => Raw(key);

    public string[] List(string key)
        => Raw(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A value that will not parse must not take the panel down. Fall back to the
    /// declared default and say so — the alternative is a provisioning request
    /// failing on a FormatException with no hint of which key is at fault.
    /// </summary>
    private T Fallback<T>(string key, Func<string, T> parse)
    {
        var def = SettingsCatalog.Find(key);
        _logger.LogWarning(
            "Setting {Key} = '{Raw}' does not parse; using the default '{Default}'.",
            key, Raw(key), def?.Default);
        return def is null ? default! : parse(def.Default);
    }
}

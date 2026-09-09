using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Settings;

/// <summary>
/// An operator's override of one configuration key.
/// </summary>
/// <remarks>
/// A row exists ONLY when someone set the value in the panel. No row means the
/// key falls through to configuration and then to the compiled default, so this
/// table is a small list of deliberate decisions rather than a copy of every
/// setting — and a key removed from the catalogue leaves nothing behind that has
/// to be cleaned up.
///
/// <para>
/// <b>Write through the API, not with SQL.</b> <see cref="SettingsStore"/> caches
/// this table in memory and reloads on write; a direct UPDATE stays invisible
/// until the process restarts.
/// </para>
/// </remarks>
public class Setting
{
    /// <summary>The configuration key, colon-separated — <c>Fleet:DefaultImage</c>.</summary>
    [Key]
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The value as a string, in the same shape the configuration provider would
    /// have supplied. Parsing is the store's job, so the two layers cannot
    /// disagree about what "5" means.
    /// </summary>
    [MaxLength(2000)]
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Which operator, so a surprising value can be asked about.</summary>
    [MaxLength(200)]
    public string? UpdatedBy { get; set; }
}

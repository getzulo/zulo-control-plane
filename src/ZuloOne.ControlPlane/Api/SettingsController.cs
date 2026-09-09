using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Settings;

namespace ZuloOne.ControlPlane.Api;

public sealed record SettingWrite(string Key, string Value);

/// <summary>
/// What an operator may change without editing a file and recreating a container.
/// </summary>
/// <remarks>
/// Every key is validated against its declaration in <see cref="SettingsCatalog"/>
/// — a key that is not declared is refused rather than stored, so this endpoint
/// cannot become a second, undocumented configuration surface.
/// </remarks>
[ApiController]
[Route("api/settings")]
[Produces("application/json")]
public class SettingsController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly SettingsStore _store;

    public SettingsController(ControlPlaneDbContext db, SettingsStore store)
    {
        _db = db;
        _store = store;
    }

    /// <summary>The whole catalogue, grouped, with the value in force and its origin.</summary>
    [HttpGet]
    public IActionResult List()
    {
        var groups = SettingsCatalog.All
            .GroupBy(d => d.Group)
            .Select(g => new
            {
                group = g.Key,
                settings = g.Select(d => new
                {
                    d.Key,
                    d.Label,
                    d.Description,
                    kind = d.Kind.ToString(),
                    d.Default,
                    d.RuntimeEditable,
                    d.Min,
                    d.Max,
                    effective = _store.Raw(d.Key),
                    // Both lower layers, so the screen can show what reverting
                    // would land on before the operator commits to it.
                    dbValue = _store.Override(d.Key),
                    configValue = _store.FromConfig(d.Key),
                    source = _store.SourceOf(d.Key).ToString(),
                }),
            });

        return Ok(new { groups });
    }

    /// <summary>Set or replace overrides. All or nothing.</summary>
    [HttpPut]
    public async Task<IActionResult> Save([FromBody] List<SettingWrite> writes, CancellationToken ct)
    {
        if (writes is null || writes.Count == 0)
            return BadRequest(new { error = "Nothing to save." });

        // Validate everything BEFORE writing anything: a half-applied set of
        // settings is a configuration nobody chose.
        var problems = new List<string>();
        foreach (var write in writes)
        {
            var def = SettingsCatalog.Find(write.Key);
            if (def is null) { problems.Add($"'{write.Key}' is not a setting this panel knows."); continue; }
            var problem = Validate(def, write.Value);
            if (problem is not null) problems.Add($"{def.Label}: {problem}");
        }
        if (problems.Count > 0) return BadRequest(new { error = string.Join(" ", problems), problems });

        var who = User.Identity?.Name
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        foreach (var write in writes)
        {
            var def = SettingsCatalog.Find(write.Key)!;
            var value = (write.Value ?? string.Empty).Trim();

            // Setting a key back to exactly what configuration says removes the
            // override instead of freezing today's config value into the database
            // — otherwise a later edit to cp.env would silently stop taking effect.
            var configValue = _store.FromConfig(def.Key);
            var matchesLowerLayer = string.Equals(value, configValue ?? def.Default, StringComparison.Ordinal);

            var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == def.Key, ct);
            if (matchesLowerLayer)
            {
                if (row is not null) _db.Settings.Remove(row);
                continue;
            }
            if (row is null)
            {
                _db.Settings.Add(new Setting { Key = def.Key, Value = value, UpdatedAt = DateTime.UtcNow, UpdatedBy = who });
            }
            else
            {
                row.Value = value;
                row.UpdatedAt = DateTime.UtcNow;
                row.UpdatedBy = who;
            }
        }

        await _db.SaveChangesAsync(ct);
        await _store.ReloadAsync(ct);
        return Ok(new { success = true, applied = writes.Count });
    }

    /// <summary>Drop the override and fall back to configuration.</summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> Revert(string key, CancellationToken ct)
    {
        var def = SettingsCatalog.Find(key);
        if (def is null) return NotFound(new { error = $"'{key}' is not a setting this panel knows." });

        var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == def.Key, ct);
        if (row is not null)
        {
            _db.Settings.Remove(row);
            await _db.SaveChangesAsync(ct);
            await _store.ReloadAsync(ct);
        }
        return Ok(new { success = true, effective = _store.Raw(def.Key), source = _store.SourceOf(def.Key).ToString() });
    }

    private static string? Validate(SettingDef def, string? raw)
    {
        var value = (raw ?? string.Empty).Trim();

        switch (def.Kind)
        {
            case SettingKind.Bool:
                return bool.TryParse(value, out _) ? null : "must be true or false.";

            case SettingKind.Int:
            case SettingKind.Bytes:
            case SettingKind.Seconds:
            case SettingKind.Hours:
            case SettingKind.Days:
                if (!long.TryParse(value, out var n)) return "must be a whole number.";
                if (def.Min is { } min && n < min) return $"must be at least {min}.";
                if (def.Max is { } max && n > max) return $"must be at most {max}.";
                return null;

            case SettingKind.Image:
                // Not a full reference parse — just the mistake that matters. An
                // image with no tag follows :latest, and a fleet where "the
                // default image" means different bytes on different days cannot be
                // reasoned about at all.
                if (value.Length == 0) return "cannot be empty.";
                var lastSegment = value[(value.LastIndexOf('/') + 1)..];
                return lastSegment.Contains(':') ? null : "must name a tag — an untagged image silently follows :latest.";

            case SettingKind.TextList:
            case SettingKind.Text:
            default:
                return value.Length > 2000 ? "is too long (2000 characters)." : null;
        }
    }
}

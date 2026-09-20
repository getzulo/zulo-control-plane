using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Settings;

namespace ZuloOne.ControlPlane.Provisioning.Demo;

/// <summary>
/// Demo policy as it is RIGHT NOW — the operator's overrides on top of <c>cp.env</c>.
/// </summary>
/// <remarks>
/// The same split <see cref="FleetConfig"/> makes, for the same reason: policy
/// comes from the settings store so the panel can change it without a container
/// recreate, while the one credential stays bound at startup. Reading a demo
/// limit through <c>IOptions</c> would freeze it at boot and make the settings
/// screen a lie.
/// </remarks>
public sealed class DemoConfig
{
    private readonly DemoSettings _bound;
    private readonly SettingsStore _store;

    public DemoConfig(IOptions<DemoSettings> bound, SettingsStore store)
    {
        _bound = bound.Value;
        _store = store;
    }

    // ---- wiring: cp.env only --------------------------------------------

    /// <summary>Shared secret the public bridge presents. Empty = the door is closed.</summary>
    public string? RequestToken => _bound.RequestToken;

    // ---- policy: overridable in the panel --------------------------------

    public bool Enabled => _store.Bool("Demo:Enabled");

    public string GoldenSlug => _store.Text("Demo:GoldenSlug");

    /// <summary>Snapshot new demos are cloned from; null until a template is blessed.</summary>
    public Guid? TemplateSnapshotId =>
        Guid.TryParse(_store.Text("Demo:TemplateSnapshotId"), out var id) && id != Guid.Empty
            ? id
            : null;

    public int PoolTarget => _store.Int("Demo:PoolTarget");

    public int MaxConcurrent => _store.Int("Demo:MaxConcurrent");

    public int MaxQueue => _store.Int("Demo:MaxQueue");

    public int LifetimeHours => _store.Int("Demo:LifetimeHours");

    public int PoolMaxAgeHours => _store.Int("Demo:PoolMaxAgeHours");

    public int MaxExtendHours => _store.Int("Demo:MaxExtendHours");

    public int ReapIntervalSeconds => _store.Int("Demo:ReapIntervalSeconds");

    public int MaxPerEmailPerDay => _store.Int("Demo:MaxPerEmailPerDay");

    public int MaxPerIpPerDay => _store.Int("Demo:MaxPerIpPerDay");

    public int PasswordWindowMinutes => _store.Int("Demo:PasswordWindowMinutes");

    public string UserName => _store.Text("Demo:UserName");

    /// <summary>
    /// What a demo container is allowed to consume.
    /// </summary>
    /// <remarks>
    /// Stored as milli-CPU rather than a decimal because the settings catalogue
    /// has no decimal kind, and adding one for a single value would be a worse
    /// trade than dividing here.
    /// </remarks>
    public ContainerLimits Limits => new(
        _store.Long("Demo:MemoryLimitBytes"),
        _store.Int("Demo:CpuMilli") / 1000m,
        _store.Int("Demo:PidsLimit"));
}

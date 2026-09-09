using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Settings;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Fleet settings as they are RIGHT NOW — the operator's overrides on top of
/// <c>cp.env</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FleetSettings"/> is bound once at startup and never changes, which
/// is why the panel telling an operator to "change Fleet__DefaultImage" meant
/// editing a file and recreating the container. The settings screen writes to a
/// table; without this type nothing would read it, and a settings screen whose
/// values are ignored is worse than none.
/// </para>
///
/// <para>
/// Only the properties declared in <see cref="SettingsCatalog"/> come from the
/// store. Everything else — the Traefik entrypoint, the networks, the container
/// port — delegates to the bound options, because those are deployment wiring
/// rather than policy and changing them at run time would describe a container
/// that does not exist.
/// </para>
/// </remarks>
public sealed class FleetConfig
{
    private readonly FleetSettings _bound;
    private readonly SettingsStore _store;

    public FleetConfig(IOptions<FleetSettings> bound, SettingsStore store)
    {
        _bound = bound.Value;
        _store = store;
    }

    // ---- policy: overridable in the panel -------------------------------

    /// <summary>Image every new tenant starts on; upgrades are per-tenant.</summary>
    public string DefaultImage => _store.Text("Fleet:DefaultImage");

    /// <summary>How long provisioning waits for a new tenant to report ready.</summary>
    public int ReadinessTimeoutSeconds => _store.Int("Fleet:ReadinessTimeoutSeconds");

    /// <summary>Per-container memory cap. 0 = unlimited.</summary>
    public long MemoryLimitBytes => _store.Long("Fleet:MemoryLimitBytes");

    /// <summary>
    /// Business-layer models a freshly provisioned tenant installs from the image
    /// it carries. Empty installs nothing; <c>*</c> installs everything.
    /// </summary>
    public string Packages => _store.Text("Fleet:Packages");

    // ---- wiring: whatever the deployment said ---------------------------

    public string RootDomain => _bound.RootDomain;
    public string EdgeNetwork => _bound.EdgeNetwork;
    public string? DataNetwork => _bound.DataNetwork;
    public string TraefikEntrypoint => _bound.TraefikEntrypoint;
    public string? TraefikCertResolver => _bound.TraefikCertResolver;
    public int ContainerPort => _bound.ContainerPort;
    public bool BehindReverseProxy => _bound.BehindReverseProxy;
    public decimal CpuLimit => _bound.CpuLimit;
    public string[] AdditionalReservedSlugs => _bound.AdditionalReservedSlugs;
}

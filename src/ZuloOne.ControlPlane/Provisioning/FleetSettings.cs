namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// How the fleet is wired: bound from the <c>Fleet</c> configuration section.
/// </summary>
public sealed class FleetSettings
{
    /// <summary>Tenants answer at <c>{slug}.{RootDomain}</c>.</summary>
    public string RootDomain { get; set; } = "zulo.one";

    /// <summary>Image every new tenant starts on; upgrades are per-tenant (§8).</summary>
    public string DefaultImage { get; set; } = "zuloone/core:dev";

    /// <summary>Docker network shared with Traefik, so it can route to the tenants.</summary>
    public string EdgeNetwork { get; set; } = "zuloone-phase0_edge";

    /// <summary>Network that reaches Postgres. Empty when the database is off-cluster.</summary>
    public string? DataNetwork { get; set; }

    /// <summary>Traefik entrypoint and cert resolver the tenant routers attach to.</summary>
    public string TraefikEntrypoint { get; set; } = "websecure";

    public string? TraefikCertResolver { get; set; } = "le";

    /// <summary>Port the tenant container listens on (the image's EXPOSE).</summary>
    public int ContainerPort { get; set; } = 8080;

    /// <summary>
    /// TLS terminates at Traefik, so tenants are reached over plain HTTP and MUST
    /// honour X-Forwarded-Proto — otherwise Core's HTTPS redirect loops forever.
    /// </summary>
    public bool BehindReverseProxy { get; set; } = true;

    /// <summary>How long provisioning waits for a new tenant to report ready.</summary>
    public int ReadinessTimeoutSeconds { get; set; } = 300;

    /// <summary>Per-container limits (§11). 0 = unlimited.</summary>
    public long MemoryLimitBytes { get; set; }

    public decimal CpuLimit { get; set; }
}

/// <summary>
/// Admin connection to the MANAGED Postgres, used only to create and drop tenant
/// databases and their owner roles. Never the connection a tenant itself uses.
/// </summary>
public sealed class TenantDatabaseSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    /// <summary>Database the admin connection lands in to issue CREATE DATABASE.</summary>
    public string AdminDatabase { get; set; } = "postgres";

    public string AdminUser { get; set; } = "postgres";

    public string? AdminPassword { get; set; }

    /// <summary>Managed providers generally require TLS.</summary>
    public bool RequireSsl { get; set; } = true;

    /// <summary>
    /// Host the TENANT uses to reach Postgres. Differs from <see cref="Host"/> when
    /// the control plane connects from outside while containers use an internal
    /// name; empty means "same as Host".
    /// </summary>
    public string? TenantHost { get; set; }
}

/// <summary>Where the set-password invitation comes from.</summary>
public sealed class ControlPlaneMailSettings
{
    public bool Enabled { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public bool UseSsl { get; set; }

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string? FromAddress { get; set; }

    public string? FromName { get; set; } = "ZuloOne";
}

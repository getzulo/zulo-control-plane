namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Operator connection to the farm Mongo. Empty URL means the journal is not
/// wired — the page still answers, with <c>disconnected</c>, so a missing
/// collector is visible rather than a 500.
/// </summary>
/// <remarks>
/// This is a secret, same class as <c>TenantDatabase:AdminPassword</c>: it stays
/// in <c>cp.env</c> / appsettings, not on the Settings screen. The user here
/// must be able to read every <c>logs_*</c> database; tenant containers get a
/// different user that can see only their own.
/// </remarks>
public sealed class TenantLogSettings
{
    public string? Url { get; set; }

    /// <summary>Default TTL when creating the <c>events</c> collection.</summary>
    public int TtlDays { get; set; } = 14;

    /// <summary>
    /// An Active tenant whose newest event is older than this is flagged
    /// "sink silent" — usually egress or a dead Serilog sink, not an empty day.
    /// </summary>
    public int SilentAfterHours { get; set; } = 3;
}

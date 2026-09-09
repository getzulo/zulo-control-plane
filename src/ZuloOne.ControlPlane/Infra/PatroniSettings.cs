namespace ZuloOne.ControlPlane.Infra;

/// <summary>
/// How the control plane reaches the database cluster. Bound from the
/// <c>Patroni</c> configuration section.
/// </summary>
public sealed class PatroniSettings
{
    /// <summary>
    /// Every node's REST endpoint. Listed rather than derived because the leader
    /// moves: reads go to whichever answers first, and they all answer the same.
    /// </summary>
    public string[] Nodes { get; set; } = [];

    /// <summary>
    /// Credentials for the UNSAFE endpoints only. Patroni protects
    /// PUT/POST/PATCH/DELETE and leaves GET open, so cluster status needs none of
    /// this and the panel can show infrastructure even when the secret is missing.
    /// </summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// Shared secret the database nodes present when publishing a health report.
    ///
    /// Low value on its own — it permits nothing but posting a report — with one
    /// exception worth naming: a holder could post a FALSE "healthy", which would
    /// mask a real outage. That is why it exists at all rather than the endpoint
    /// being open.
    /// </summary>
    public string? ReportToken { get; set; }

    /// <summary>
    /// How long a node's last report stays believable. Beyond this the panel shows
    /// the node as unheard-from rather than as whatever it last claimed. Three
    /// times the script's five-minute timer, so a single missed run is not an alarm.
    /// </summary>
    public int ReportStaleAfterMinutes { get; set; } = 15;
}

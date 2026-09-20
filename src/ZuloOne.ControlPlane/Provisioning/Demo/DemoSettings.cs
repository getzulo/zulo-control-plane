namespace ZuloOne.ControlPlane.Provisioning.Demo;

/// <summary>
/// The one demo setting that is NOT in the settings catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequestToken"/> is the shared secret guarding the only anonymous
/// write path into the panel. It lives in <c>cp.env</c> for the same reason
/// <c>Access:*</c> and <c>Patroni:ReportToken</c> do: a credential that admits
/// the public must not be editable from a screen that credential could one day
/// reach.
/// </para>
/// <para>
/// Empty is a meaningful value — it means the public door is closed, and the
/// endpoint answers 503 rather than accepting anonymously. That is the same
/// decision <c>InfraController.Report</c> already makes for node health, and
/// the reason is identical: an unconfigured secret must fail shut.
/// </para>
/// </remarks>
public sealed class DemoSettings
{
    public string? RequestToken { get; set; }
}

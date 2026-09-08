namespace ZuloOne.ControlPlane.Auth;

/// <summary>
/// Cloudflare Access — the normal way in, bound from the <c>Access</c> section.
/// </summary>
public sealed class AccessSettings
{
    /// <summary>e.g. <c>https://yourteam.cloudflareaccess.com</c>. Also the token issuer.</summary>
    public string? TeamDomain { get; set; }

    /// <summary>
    /// The Application Audience tag from the Access application, a 64-character
    /// hex string. It is what ties a token to THIS application rather than to any
    /// other application in the same Cloudflare account.
    /// </summary>
    public string? Aud { get; set; }

    /// <summary>
    /// Who may sign in, checked against the token's <c>email</c> claim.
    ///
    /// <para>
    /// This is the cheap AND that makes it safe for either scheme to be sufficient
    /// on its own. If the Access policy is ever left broader than intended — set to
    /// "everyone" during setup, or pointed at the wrong AUD — then without this,
    /// anyone holding a matching token reaches an API that creates and drops
    /// databases. Registration is refused outright when this list is empty, because
    /// "temporarily empty for testing" is exactly the configuration that ships.
    /// </para>
    /// </summary>
    public string[] AllowedEmails { get; set; } = [];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TeamDomain)
        && !string.IsNullOrWhiteSpace(Aud)
        && AllowedEmails.Length > 0;
}

/// <summary>
/// The break-glass operator, bound from the <c>Operator</c> section.
/// </summary>
public sealed class OperatorSettings
{
    public string? Email { get; set; }

    /// <summary>
    /// A BCrypt hash, never a password. Storing the hash is what makes the
    /// configuration file merely sensitive instead of a credential — someone who
    /// reads it still cannot sign in.
    /// </summary>
    public string? PasswordHash { get; set; }

    public int SessionHours { get; set; } = 8;

    public int LockoutThreshold { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    /// <summary>
    /// The loopback port that carries the break-glass listener. Password login and
    /// enrolment are refused on any other port.
    ///
    /// <para>
    /// A much stronger statement than a rate limit: the password endpoint is not
    /// reachable from the internet at all, only through an SSH tunnel to the host.
    /// The public listener serves the API and the dashboard and has no way to
    /// accept a password.
    /// </para>
    /// </summary>
    public int BreakGlassPort { get; set; } = 5099;

    /// <summary>Relax the port check for local development only.</summary>
    public bool RequireBreakGlassPort { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(PasswordHash);
}

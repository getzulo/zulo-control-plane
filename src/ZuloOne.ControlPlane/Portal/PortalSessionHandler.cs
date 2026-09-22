using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Portal;

/// <summary>
/// Validates a customer's bearer token against <see cref="CustomerSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mechanically the twin of <see cref="Auth.OperatorSessionHandler"/> — opaque
/// token, hash compared in the database, row deleted on expiry — and that
/// similarity is the point worth stating plainly: <b>this scheme is never added to
/// the panel's authorization policy.</b>
/// </para>
/// <para>
/// <c>AuthSetup</c> builds its policy from an explicit list of schemes, and the
/// customer scheme is not in it. So a customer token presented to
/// <c>/api/tenants</c> does not authenticate: the scheme that could validate it is
/// never asked, and the schemes that are asked cannot. That is structural rather
/// than a check someone has to remember to write in each controller — which is why
/// the portal is a separate scheme at all instead of a claim on the operator one.
/// </para>
/// <para>
/// It holds in the other direction too, and that is deliberate: an operator
/// session cannot drive <c>/api/portal</c> either. An operator who needs to act on
/// a tenant has richer tools on their own side, and letting staff credentials
/// operate the customer surface would make the portal's audit trail unable to
/// answer who actually pressed the button.
/// </para>
/// </remarks>
public sealed class PortalSessionHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PortalSession";

    /// <summary>Claim carrying the customer account id.</summary>
    public const string AccountIdClaim = "zuloone.portal.account";

    private readonly ControlPlaneDbContext _db;
    private readonly PortalSettings _settings;

    public PortalSessionHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ControlPlaneDbContext db,
        IOptions<PortalSettings> settings)
        : base(options, logger, encoder)
    {
        _db = db;
        _settings = settings.Value;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // A disabled portal authenticates nobody. Without this, turning Enabled off
        // would stop new sign-ins while every session already issued kept working —
        // which is not what "off" means to whoever switched it.
        if (!_settings.Enabled) return AuthenticateResult.NoResult();

        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0) return AuthenticateResult.NoResult();

        byte[] hash;
        try { hash = SHA256.HashData(PortalTokens.Decode(token)); }
        catch { return AuthenticateResult.NoResult(); }

        var session = await _db.CustomerSessions
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.TokenHash == hash);

        if (session is null || !CryptographicOperations.FixedTimeEquals(session.TokenHash, hash))
            return AuthenticateResult.Fail("Unknown session.");

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            _db.CustomerSessions.Remove(session);
            await _db.SaveChangesAsync();
            return AuthenticateResult.Fail("Session expired.");
        }

        // An account whose verification was revoked, or which was locked after the
        // session was minted, must stop working now rather than at expiry. This is
        // the reason the lookup is here at all.
        if (session.Account is null || session.Account.EmailVerifiedAt is null)
            return AuthenticateResult.Fail("Account is not verified.");

        if (session.Account.LockedUntil is { } until && until > DateTime.UtcNow)
            return AuthenticateResult.Fail("Account is locked.");

        session.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.CustomerAccountId.ToString()),
            new Claim(AccountIdClaim, session.CustomerAccountId.ToString()),
            new Claim(ClaimTypes.Email, session.Account.Email),
            new Claim("zuloone.cp.mode", "portal"),
        ], SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

/// <summary>
/// Minting and encoding for every secret the portal hands out: sessions,
/// verification links, reset links.
/// </summary>
/// <remarks>
/// One place, because all three are the same object — 32 random bytes, stored as
/// their SHA-256 — and three hand-rolled copies is how one of them ends up with 16
/// bytes or a non-cryptographic generator, in the copy nobody reviewed.
/// </remarks>
public static class PortalTokens
{
    /// <summary>A fresh secret and the hash to store for it.</summary>
    public static (string Token, byte[] Hash) Mint()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return (Encode(bytes), SHA256.HashData(bytes));
    }

    /// <summary>The hash to look up for a secret somebody presented.</summary>
    public static byte[] HashOf(string token) => SHA256.HashData(Decode(token));

    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

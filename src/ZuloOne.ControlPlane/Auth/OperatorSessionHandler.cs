using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Auth;

/// <summary>
/// Validates a break-glass bearer token against <see cref="OperatorSession"/>.
///
/// <para>
/// The lookup on every request is the feature, not the cost. It is what makes
/// deleting a session row stop the token immediately — the property a stateless
/// JWT cannot have, and which the platform's session table only appears to have.
/// </para>
/// </summary>
public sealed class OperatorSessionHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "OperatorSession";

    private readonly ControlPlaneDbContext _db;

    public OperatorSessionHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ControlPlaneDbContext db)
        : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0) return AuthenticateResult.NoResult();

        byte[] hash;
        try { hash = SHA256.HashData(Base64UrlDecode(token)); }
        catch { return AuthenticateResult.NoResult(); }

        // Compared as bytes in the database. The candidate is already a hash of
        // whatever was presented, so a timing difference here reveals nothing about
        // the token itself — but the equality below is still fixed-time, because
        // arguing about which comparisons are safe is more expensive than just
        // making them all safe.
        var session = await _db.OperatorSessions
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.TokenHash == hash);

        if (session is null || !CryptographicOperations.FixedTimeEquals(session.TokenHash, hash))
            return AuthenticateResult.Fail("Unknown session.");

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            // Remove rather than leave it: an expired row is only useful to
            // whoever eventually reads this table.
            _db.OperatorSessions.Remove(session);
            await _db.SaveChangesAsync();
            return AuthenticateResult.Fail("Session expired.");
        }

        session.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.OperatorAccountId.ToString()),
            new Claim(ClaimTypes.Email, session.Account?.Email ?? string.Empty),
            new Claim("zuloone.cp.mode", "local"),
        ], SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

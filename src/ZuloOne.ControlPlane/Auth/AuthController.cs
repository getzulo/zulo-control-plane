using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtpNet;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Auth;

public record LoginRequest(string Email, string Password, string Totp);
public record EnrolRequest(string Email, string Password, string? Totp);

/// <summary>
/// The break-glass path, and the one endpoint the dashboard uses to find out which
/// way it got in.
/// </summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly OperatorSettings _settings;
    private readonly ILogger<AuthController> _logger;

    public AuthController(ControlPlaneDbContext db, IOptions<OperatorSettings> settings, ILogger<AuthController> logger)
    {
        _db = db;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// What mode this request arrived in. Anonymous, and called by the dashboard
    /// before it renders anything.
    ///
    /// <para>
    /// The dashboard must ASK rather than infer. If it decided from the URL or from
    /// a build-time flag that it is behind Access, then over the break-glass tunnel
    /// it would render, get 401, and — following the platform's client, which
    /// redirects to /login unconditionally — bounce off a route that does not
    /// exist, serve the SPA fallback, and loop. That would be discovered over an
    /// SSH tunnel during whatever outage sent you there.
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpGet("context")]
    public async Task<IActionResult> Context()
    {
        var account = await _db.OperatorAccounts.AsNoTracking().FirstOrDefaultAsync();
        var mode = User.FindFirst("zuloone.cp.mode")?.Value;

        return Ok(new
        {
            authenticated = User.Identity?.IsAuthenticated == true,
            email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                    ?? User.FindFirst("email")?.Value,
            mode = mode ?? "anonymous",
            localLoginAvailable = OnBreakGlassPort() && account is not null,
            enrolled = account?.TotpSecret is not null,
        });
    }

    [AllowAnonymous]
    [EnableRateLimiting("operator-login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!OnBreakGlassPort()) return NotFound();

        var account = await _db.OperatorAccounts.FirstOrDefaultAsync(a => a.Email == request.Email);
        // One message for every failure below. Distinguishing them is what turns a
        // login form into an oracle for which accounts exist and which are locked.
        var deny = Unauthorized(new { error = "Invalid credentials." });
        if (account is null) return deny;

        // BEFORE the password is verified — see OperatorAccount.LockedUntil. Also
        // stops a locked account from being able to spend a core on bcrypt.
        if (account.LockedUntil is { } until && until > DateTime.UtcNow) return deny;

        if (!BCrypt.Net.BCrypt.Verify(request.Password, account.PasswordHash))
            return await FailAsync(account, deny);

        // An account with a password and no second factor must not be loggable-in.
        // Refusing here is what keeps "mandatory TOTP" from degrading to "TOTP if
        // someone got round to enrolling".
        if (account.TotpSecret is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "This account has no second factor enrolled. Complete enrolment first." });

        var totp = new Totp(account.TotpSecret);
        if (!totp.VerifyTotp(request.Totp ?? string.Empty, out var step, new VerificationWindow(previous: 1, future: 1)))
            return await FailAsync(account, deny);

        // Replay guard. Otp.NET happily accepts the same code for its whole window,
        // so a code seen once — over a shoulder, in a terminal's scrollback — is
        // reusable without this.
        if (account.LastTotpStep is { } last && step <= last)
        {
            _logger.LogWarning("Replayed TOTP step {Step} for {Email}", step, account.Email);
            return await FailAsync(account, deny);
        }

        var token = RandomNumberGenerator.GetBytes(32);
        _db.OperatorSessions.Add(new OperatorSession
        {
            OperatorAccountId = account.Id,
            TokenHash = SHA256.HashData(token),
            ExpiresAt = DateTime.UtcNow.AddHours(Math.Max(1, _settings.SessionHours)),
            CreatedFromIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });

        account.LastTotpStep = step;
        account.FailedAttempts = 0;
        account.LockedUntil = null;
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Break-glass sign-in by {Email}", account.Email);
        return Ok(new
        {
            token = OperatorSessionHandler.Base64UrlEncode(token),
            expiresAt = DateTime.UtcNow.AddHours(Math.Max(1, _settings.SessionHours)),
            email = account.Email,
        });
    }

    /// <summary>
    /// Step one of enrolment: prove the password, receive a secret to scan. The
    /// secret is NOT saved yet — a half-scanned QR would otherwise brick the only
    /// account that works when Cloudflare does not.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("operator-login")]
    [HttpPost("enrol/begin")]
    public async Task<IActionResult> EnrolBegin([FromBody] EnrolRequest request)
    {
        var account = await AuthenticateForEnrolAsync(request);
        if (account is null) return Unauthorized(new { error = "Invalid credentials." });
        if (account.TotpSecret is not null) return BadRequest(new { error = "Already enrolled." });

        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32 = Base32Encoding.ToString(secret);
        // Held in the session cache rather than the database for the same reason as
        // above: nothing is persisted until a code from it verifies.
        HttpContext.Response.Headers["Cache-Control"] = "no-store";
        return Ok(new
        {
            secret = base32,
            uri = $"otpauth://totp/ZuloOne%20Control%20Plane:{Uri.EscapeDataString(account.Email)}?secret={base32}&issuer=ZuloOne",
        });
    }

    /// <summary>Step two: a code from the secret proves the authenticator has it.</summary>
    [AllowAnonymous]
    [EnableRateLimiting("operator-login")]
    [HttpPost("enrol/confirm")]
    public async Task<IActionResult> EnrolConfirm([FromBody] EnrolConfirmRequest request)
    {
        var account = await AuthenticateForEnrolAsync(new EnrolRequest(request.Email, request.Password, request.Totp));
        if (account is null) return Unauthorized(new { error = "Invalid credentials." });
        if (account.TotpSecret is not null) return BadRequest(new { error = "Already enrolled." });

        byte[] secret;
        try { secret = Base32Encoding.ToBytes(request.Secret); }
        catch { return BadRequest(new { error = "Malformed secret." }); }

        var totp = new Totp(secret);
        if (!totp.VerifyTotp(request.Totp ?? string.Empty, out var step, new VerificationWindow(previous: 1, future: 1)))
            return BadRequest(new { error = "That code does not match the secret — rescan and try again." });

        account.TotpSecret = secret;
        account.LastTotpStep = step;
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Never log the secret itself: stdout is aggregated, shipped and backed up,
        // and a second factor sitting in a log stream is not a second factor.
        _logger.LogInformation("Break-glass second factor enrolled for {Email}", account.Email);
        return Ok(new { enrolled = true });
    }

    /// <summary>Ends this session. Deleting the row is what makes the token stop working.</summary>
    [Authorize(AuthenticationSchemes = OperatorSessionHandler.SchemeName)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var hash = SHA256.HashData(OperatorSessionHandler.Base64UrlDecode(header["Bearer ".Length..].Trim()));
            var session = await _db.OperatorSessions.FirstOrDefaultAsync(s => s.TokenHash == hash);
            if (session is not null)
            {
                _db.OperatorSessions.Remove(session);
                await _db.SaveChangesAsync();
            }
        }
        return Ok(new { success = true });
    }

    private async Task<OperatorAccount?> AuthenticateForEnrolAsync(EnrolRequest request)
    {
        if (!OnBreakGlassPort()) return null;
        var account = await _db.OperatorAccounts.FirstOrDefaultAsync(a => a.Email == request.Email);
        if (account is null) return null;
        if (account.LockedUntil is { } until && until > DateTime.UtcNow) return null;
        if (!BCrypt.Net.BCrypt.Verify(request.Password, account.PasswordHash))
        {
            await FailAsync(account, Unauthorized());
            return null;
        }
        return account;
    }

    private async Task<IActionResult> FailAsync(OperatorAccount account, IActionResult result)
    {
        account.FailedAttempts++;
        if (account.FailedAttempts >= Math.Max(1, _settings.LockoutThreshold))
        {
            account.LockedUntil = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.LockoutMinutes));
            account.FailedAttempts = 0;
            _logger.LogWarning("Locked break-glass account {Email} until {Until}", account.Email, account.LockedUntil);
        }
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return result;
    }

    /// <summary>
    /// True when the request came in on the loopback listener rather than the
    /// public one. Password authentication exists only there, which is a stronger
    /// statement than any rate limit: the endpoint is not reachable from the
    /// internet, only through an SSH tunnel to the host.
    /// </summary>
    private bool OnBreakGlassPort() =>
        !_settings.RequireBreakGlassPort
        || HttpContext.Connection.LocalPort == _settings.BreakGlassPort;
}

public record EnrolConfirmRequest(string Email, string Password, string Secret, string Totp);

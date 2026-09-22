using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtpNet;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Portal;

public record PortalRegisterRequest(string Email, string Password, string? DisplayName, string? Locale);
public record PortalLoginRequest(string Email, string Password, string? Totp);
public record PortalTokenRequest(string Token);
public record PortalResetRequest(string Token, string Password);
public record PortalForgotRequest(string Email);
public record PortalChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// How a customer gets in: register, prove the address, sign in, get back in when
/// they have forgotten how.
/// </summary>
/// <remarks>
/// Every refusal below answers the same way on purpose. A registration form that
/// says "already taken" and a reset form that says "no such address" together
/// enumerate the customer list for anybody with a script, and a customer list is
/// exactly what a competitor would like. So both always answer "check your
/// mailbox", and the mailbox is what distinguishes the cases — to the one person
/// entitled to know.
/// </remarks>
[ApiController]
[Route("api/portal/auth")]
[Produces("application/json")]
public class PortalAuthController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly PortalSettings _settings;
    private readonly PortalMailer _mailer;
    private readonly TenantClaimService _claims;
    private readonly ILogger<PortalAuthController> _logger;

    public PortalAuthController(
        ControlPlaneDbContext db,
        IOptions<PortalSettings> settings,
        PortalMailer mailer,
        TenantClaimService claims,
        ILogger<PortalAuthController> logger)
    {
        _db = db;
        _settings = settings.Value;
        _mailer = mailer;
        _claims = claims;
        _logger = logger;
    }

    /// <summary>
    /// Whether the portal is on, and who this request is. Anonymous: the UI calls
    /// it before it renders anything, exactly as the operator dashboard does.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("context")]
    public IActionResult Context()
    {
        if (!_settings.Enabled) return NotFound();

        return Ok(new
        {
            authenticated = User.Identity?.IsAuthenticated == true,
            email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
            // So the sign-up form can say what a password has to be before the
            // person types one, rather than after.
            minPasswordLength = MinPasswordLength,
        });
    }

    [AllowAnonymous]
    [EnableRateLimiting(PortalRateLimit.Policy)]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] PortalRegisterRequest request, CancellationToken ct)
    {
        if (!_settings.Enabled) return NotFound();

        var email = Normalise(request.Email);
        if (email is null) return BadRequest(new { error = "That does not look like an e-mail address." });
        if (PasswordComplaint(request.Password, email) is { } complaint)
            return BadRequest(new { error = complaint });

        var existing = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Email == email, ct);

        if (existing is null)
        {
            var account = new CustomerAccount
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                DisplayName = Trim(request.DisplayName, 200),
                Locale = Trim(request.Locale, 16),
            };
            _db.CustomerAccounts.Add(account);
            await _db.SaveChangesAsync(ct);
            var fresh = await IssueAsync(account, CustomerTokenKind.VerifyEmail, ct);
            await _mailer.SendVerificationAsync(account.Email, account.DisplayName, fresh, ct);
            _logger.LogInformation("Portal registration for {Email}", email);
        }
        else if (existing.EmailVerifiedAt is null)
        {
            // Unverified and registering again is almost always "the first mail
            // never arrived". Replacing the password is safe here precisely
            // BECAUSE the address is unproven: there is no account anyone could be
            // locked out of yet, and no session to hijack.
            existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            existing.DisplayName = Trim(request.DisplayName, 200) ?? existing.DisplayName;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            var again = await IssueAsync(existing, CustomerTokenKind.VerifyEmail, ct);
            await _mailer.SendVerificationAsync(existing.Email, existing.DisplayName, again, ct);
        }
        else
        {
            // A verified account already exists. Its password must NOT be touched —
            // this request could be from anyone. Send the letter that helps the
            // real owner and tells an impostor nothing.
            var token = await IssueAsync(existing, CustomerTokenKind.ResetPassword, ct);
            await _mailer.SendPasswordResetAsync(existing.Email, token, ct);
            _logger.LogInformation("Portal registration for an existing address {Email} — sent a reset instead", email);
        }

        // One answer for all three branches.
        return Ok(new { sent = true, message = "If that address can be registered, a message is on its way to it." });
    }

    [AllowAnonymous]
    [EnableRateLimiting(PortalRateLimit.Policy)]
    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] PortalTokenRequest request, CancellationToken ct)
    {
        if (!_settings.Enabled) return NotFound();

        var token = await ConsumeAsync(request.Token, CustomerTokenKind.VerifyEmail, ct);
        if (token?.Account is null)
            return BadRequest(new { error = "That link has expired or has already been used. Ask for a new one." });

        token.Account.EmailVerifiedAt ??= DateTime.UtcNow;
        token.Account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var claimed = await _claims.ClaimAsync(token.Account, ct);

        _logger.LogInformation("Portal address verified for {Email}, claimed {Count} stand(s)", token.Account.Email, claimed);
        return Ok(new { verified = true, claimed });
    }

    [AllowAnonymous]
    [EnableRateLimiting(PortalRateLimit.Policy)]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] PortalLoginRequest request, CancellationToken ct)
    {
        if (!_settings.Enabled) return NotFound();

        var email = Normalise(request.Email);
        var deny = Unauthorized(new { error = "Invalid credentials." });
        if (email is null) return deny;

        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (account is null) return deny;

        // Before the password check, for both of the operator account's reasons:
        // it stops the answer distinguishing "wrong password" from "right password,
        // locked", and it stops a locked account spending a core on bcrypt.
        if (account.LockedUntil is { } until && until > DateTime.UtcNow) return deny;

        if (!BCrypt.Net.BCrypt.Verify(request.Password, account.PasswordHash))
            return await FailAsync(account, deny, ct);

        // Deliberately a DIFFERENT answer from wrong credentials, and deliberately
        // only after the password has been proved. Saying "not verified" before
        // that would confirm the address exists; saying it after has told the
        // person nothing they did not already know, and it is the only way they
        // can find out why a correct password is not working.
        if (account.EmailVerifiedAt is null)
        {
            var fresh = await IssueAsync(account, CustomerTokenKind.VerifyEmail, ct);
            await _mailer.SendVerificationAsync(account.Email, account.DisplayName, fresh, ct);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "This address has not been confirmed yet. We have sent the link again.",
                unverified = true,
            });
        }

        if (account.TotpSecret is not null)
        {
            var totp = new Totp(account.TotpSecret);
            if (!totp.VerifyTotp(request.Totp ?? string.Empty, out var step, new VerificationWindow(previous: 1, future: 1)))
                return await FailAsync(account, deny, ct);

            // Replay guard: Otp.NET accepts a code for its whole window, so one
            // seen over a shoulder is reusable without this.
            if (account.LastTotpStep is { } last && step <= last)
                return await FailAsync(account, deny, ct);

            account.LastTotpStep = step;
        }

        var (token, hash) = PortalTokens.Mint();
        var expires = DateTime.UtcNow.AddHours(Math.Max(1, _settings.SessionHours));
        _db.CustomerSessions.Add(new CustomerSession
        {
            CustomerAccountId = account.Id,
            TokenHash = hash,
            ExpiresAt = expires,
            CreatedFromIp = ClientIp(),
        });

        account.FailedAttempts = 0;
        account.LockedUntil = null;
        account.LastSignedInAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Here too, not only at verification: a stand provisioned after the account
        // was made would otherwise never be picked up. See TenantClaimService.
        await _claims.ClaimAsync(account, ct);

        return Ok(new { token, expiresAt = expires, email = account.Email, displayName = account.DisplayName });
    }

    [AllowAnonymous]
    [EnableRateLimiting(PortalRateLimit.Policy)]
    [HttpPost("forgot")]
    public async Task<IActionResult> Forgot([FromBody] PortalForgotRequest request, CancellationToken ct)
    {
        if (!_settings.Enabled) return NotFound();

        var email = Normalise(request.Email);
        if (email is not null)
        {
            var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Email == email, ct);
            if (account is not null)
            {
                var token = await IssueAsync(account, CustomerTokenKind.ResetPassword, ct);
                await _mailer.SendPasswordResetAsync(account.Email, token, ct);
            }
        }

        // Identical whether or not anything happened, including for a malformed
        // address — a 400 here would be as good an oracle as a 404.
        return Ok(new { sent = true, message = "If that address has an account, a reset link is on its way to it." });
    }

    [AllowAnonymous]
    [EnableRateLimiting(PortalRateLimit.Policy)]
    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] PortalResetRequest request, CancellationToken ct)
    {
        if (!_settings.Enabled) return NotFound();

        var token = await ConsumeAsync(request.Token, CustomerTokenKind.ResetPassword, ct);
        if (token?.Account is null)
            return BadRequest(new { error = "That link has expired or has already been used. Ask for a new one." });

        if (PasswordComplaint(request.Password, token.Account.Email) is { } complaint)
        {
            // The token was consumed above and is not given back. Deliberate: a
            // link that survives a bad attempt is a link an attacker can keep
            // trying against. The cost is one more mail for a legitimate typo.
            return BadRequest(new { error = complaint });
        }

        token.Account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        // A reset proves the mailbox, which is the same proof verification asks
        // for. Refusing to verify here would leave somebody who lost their
        // password before clicking the first link permanently unable to get in.
        token.Account.EmailVerifiedAt ??= DateTime.UtcNow;
        token.Account.FailedAttempts = 0;
        token.Account.LockedUntil = null;
        token.Account.UpdatedAt = DateTime.UtcNow;

        // Every existing session dies. This is the point of a reset: the case it
        // exists for is "somebody else may be in my account", and a new password
        // that leaves their session alive does not answer that at all.
        await _db.CustomerSessions
            .Where(s => s.CustomerAccountId == token.CustomerAccountId)
            .ExecuteDeleteAsync(ct);

        // And every other outstanding reset link, for the same reason.
        await _db.CustomerTokens
            .Where(t => t.CustomerAccountId == token.CustomerAccountId
                        && t.Kind == CustomerTokenKind.ResetPassword
                        && t.ConsumedAt == null)
            .ExecuteDeleteAsync(ct);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Portal password reset completed for {Email}", token.Account.Email);
        return Ok(new { reset = true });
    }

    [Authorize(Policy = AuthSetup.PortalPolicy)]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] PortalChangePasswordRequest request, CancellationToken ct)
    {
        if (!_settings.Enabled) return NotFound();

        var account = await CurrentAccountAsync(ct);
        if (account is null) return Unauthorized();

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, account.PasswordHash))
            return BadRequest(new { error = "The current password is not right." });

        if (PasswordComplaint(request.NewPassword, account.Email) is { } complaint)
            return BadRequest(new { error = complaint });

        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        account.UpdatedAt = DateTime.UtcNow;

        // Other sessions only. Signing somebody out of the browser they are
        // actively using to change their password is a bug, not a precaution.
        var keep = CurrentSessionHash();
        await _db.CustomerSessions
            .Where(s => s.CustomerAccountId == account.Id && (keep == null || s.TokenHash != keep))
            .ExecuteDeleteAsync(ct);

        await _db.SaveChangesAsync(ct);
        return Ok(new { changed = true });
    }

    [Authorize(Policy = AuthSetup.PortalPolicy)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (!_settings.Enabled) return NotFound();

        if (CurrentSessionHash() is { } hash)
        {
            await _db.CustomerSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(ct);
        }
        return Ok(new { success = true });
    }

    // ------------------------------------------------------------------ bits ---

    internal const int MinPasswordLength = 12;

    /// <summary>
    /// Lower-cased and trimmed, or null if it cannot be an address.
    /// </summary>
    /// <remarks>
    /// Lower-casing is not tidiness: the column is unique, and "Anna@x" versus
    /// "anna@x" would otherwise be two accounts for one person — one of which owns
    /// the stand and one of which mysteriously does not.
    /// </remarks>
    private static string? Normalise(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value.Length > 320) return null;

        // Deliberately not a full RFC 5322 grammar. The address is proved by a
        // letter arriving at it; a stricter pattern here only rejects valid
        // addresses that happen to be unusual.
        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1) return null;
        if (value.IndexOf('@', at + 1) >= 0) return null;
        if (!value[(at + 1)..].Contains('.')) return null;
        if (value.Contains(' ')) return null;
        return value;
    }

    /// <summary>The reason this password is not acceptable, or null.</summary>
    private static string? PasswordComplaint(string? password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"Use at least {MinPasswordLength} characters.";
        if (password.Length > 200)
            return "That is longer than 200 characters.";
        // BCrypt truncates at 72 bytes, so a longer password is silently only its
        // first 72 — worth refusing rather than accepting a promise we do not keep.
        if (System.Text.Encoding.UTF8.GetByteCount(password) > 72)
            return "That is too long — keep it under 72 characters.";
        if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
            return "Your password cannot be your e-mail address.";
        return null;
    }

    private static string? Trim(string? value, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length <= max ? v : v[..max];
    }

    /// <summary>Mints a one-shot token, stores its hash and returns the secret.</summary>
    private async Task<string> IssueAsync(CustomerAccount account, CustomerTokenKind kind, CancellationToken ct)
    {
        // Outstanding tokens of the same kind are dropped. Two live verification
        // links for one address means the older mail still works after the newer
        // one was sent, which is a longer window than anybody intended.
        await _db.CustomerTokens
            .Where(t => t.CustomerAccountId == account.Id && t.Kind == kind && t.ConsumedAt == null)
            .ExecuteDeleteAsync(ct);

        var (token, hash) = PortalTokens.Mint();
        _db.CustomerTokens.Add(new CustomerToken
        {
            CustomerAccountId = account.Id,
            Kind = kind,
            TokenHash = hash,
            ExpiresAt = kind == CustomerTokenKind.VerifyEmail
                ? DateTime.UtcNow.AddHours(Math.Max(1, _settings.VerifyTokenHours))
                : DateTime.UtcNow.AddMinutes(Math.Max(5, _settings.ResetTokenMinutes)),
        });
        await _db.SaveChangesAsync(ct);

        // Mints only — every caller sends its own letter. Sending from in here for
        // one kind and not the other reads fine until a caller that already sends
        // is pointed at the kind that also sends, and the person gets two mails
        // carrying two different tokens, one of which the other has just deleted.
        return token;
    }

    /// <summary>
    /// Finds a live token, marks it used and returns it with its account. Null for
    /// anything expired, already used, of the wrong kind, or simply not a token.
    /// </summary>
    private async Task<CustomerToken?> ConsumeAsync(string? presented, CustomerTokenKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presented)) return null;

        byte[] hash;
        try { hash = PortalTokens.HashOf(presented); }
        catch { return null; }

        var token = await _db.CustomerTokens
            .Include(t => t.Account)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == kind, ct);

        if (token is null || token.ConsumedAt is not null || token.ExpiresAt <= DateTime.UtcNow) return null;
        if (!CryptographicOperations.FixedTimeEquals(token.TokenHash, hash)) return null;

        token.ConsumedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return token;
    }

    private async Task<IActionResult> FailAsync(CustomerAccount account, IActionResult result, CancellationToken ct)
    {
        account.FailedAttempts++;
        if (account.FailedAttempts >= Math.Max(1, _settings.LockoutThreshold))
        {
            account.LockedUntil = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.LockoutMinutes));
            account.FailedAttempts = 0;
            _logger.LogWarning("Locked portal account {Email} until {Until}", account.Email, account.LockedUntil);
        }
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<CustomerAccount?> CurrentAccountAsync(CancellationToken ct)
    {
        var raw = User.FindFirst(PortalSessionHandler.AccountIdClaim)?.Value;
        return Guid.TryParse(raw, out var id)
            ? await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            : null;
    }

    private byte[]? CurrentSessionHash()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        try { return PortalTokens.HashOf(header["Bearer ".Length..].Trim()); }
        catch { return null; }
    }

    private string? ClientIp() =>
        Request.Headers["CF-Connecting-IP"].FirstOrDefault()
        ?? HttpContext.Connection.RemoteIpAddress?.ToString();
}

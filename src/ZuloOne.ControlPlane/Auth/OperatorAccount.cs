using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Auth;

/// <summary>
/// The break-glass operator: one account, seeded from configuration, used when
/// Cloudflare Access cannot be reached.
///
/// <para>
/// Deliberately not a user system. There is no registration, no password reset by
/// e-mail and no second account — recovery is access to the host, which is the
/// only thing still available in the outage this exists for. Every feature that
/// would make it more convenient would also make it a larger attack surface than
/// the thing it is guarding.
/// </para>
/// </summary>
public class OperatorAccount
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    /// <summary>BCrypt. Seeded as a hash from configuration — never a plaintext password.</summary>
    [Required]
    [MaxLength(200)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// The TOTP shared secret, null until enrolment completes. An account with a
    /// password and no second factor must never be able to sign in — see the
    /// refusal in AuthController — or "mandatory" quietly becomes "optional".
    /// </summary>
    public byte[]? TotpSecret { get; set; }

    /// <summary>
    /// The last accepted TOTP time step.
    ///
    /// Otp.NET verifies a code but does nothing about replay: a code stays valid
    /// for its whole window, so anything that observes one — a shoulder, a
    /// compromised terminal, a proxy log — can reuse it. Recording the step and
    /// refusing anything at or below it is what makes a code single-use.
    /// </summary>
    public long? LastTotpStep { get; set; }

    public int FailedAttempts { get; set; }

    /// <summary>
    /// Set once <see cref="FailedAttempts"/> crosses the threshold, and checked
    /// BEFORE the password is verified. That ordering is the whole point: checking
    /// afterwards makes the response distinguish "wrong password" from "right
    /// password, locked account", which turns the login form into an oracle. It
    /// also stops a locked account from being able to burn CPU on bcrypt.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A signed-in break-glass session.
///
/// <para>
/// An opaque token rather than a JWT, chosen because revocation has to actually
/// work. A stateless JWT can only be revoked by adding a database lookup on every
/// request — at which point you have built this table anyway, and are still
/// carrying a signing key, clock skew and a token library. The platform's own
/// session table is the cautionary example: it exists, it has an IsActive flag,
/// and clearing that flag stops nothing.
/// </para>
///
/// <para>
/// Only the SHA-256 of the token is stored. The registry database is the one
/// whose compromise this whole task is about, and a hash means reading it does not
/// yield a usable session.
/// </para>
/// </summary>
public class OperatorSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OperatorAccountId { get; set; }

    public OperatorAccount? Account { get; set; }

    /// <summary>SHA-256 of the bearer token. Never the token itself.</summary>
    [Required]
    [MaxLength(32)]
    public byte[] TokenHash { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? CreatedFromIp { get; set; }
}

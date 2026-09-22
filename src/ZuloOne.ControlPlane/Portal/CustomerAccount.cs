using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Portal;

/// <summary>
/// Somebody who bought a stand, signing in to look after it themselves.
///
/// <para>
/// The deliberate opposite of <see cref="Auth.OperatorAccount"/>, which documents
/// itself as "not a user system": no registration, no reset by e-mail, one account.
/// That is right for break-glass, where every convenience widens the surface
/// guarding everything else. It is wrong here. A customer who cannot reset their
/// own password writes to support, and the whole point of this table is that they
/// should not have to.
/// </para>
///
/// <para>
/// So the two are separate tables with separate sessions and separate schemes,
/// and neither can ever authenticate as the other. That separation is the security
/// design; see <see cref="PortalSessionHandler"/> for where it is enforced.
/// </para>
/// </summary>
public class CustomerAccount
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Lower-cased on write, and the unique key. Addresses are handed to us by
    /// people typing them, so "Anna@..." and "anna@..." are the same person and
    /// must not become two accounts — one of which would then own the tenant and
    /// the other would not, with no visible difference between them.
    /// </summary>
    [Required]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    /// <summary>BCrypt, at the library's default work factor.</summary>
    [Required]
    [MaxLength(200)]
    public string PasswordHash { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>Which language they signed up in, so mail reaches them in it.</summary>
    [MaxLength(16)]
    public string? Locale { get; set; }

    /// <summary>
    /// When the address was proven. Null means unproven, and an unproven account
    /// must not sign in.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. Tenant ownership is claimed by matching a verified address
    /// against <see cref="Registry.Tenant.AdminEmail"/> — so an unverified address
    /// would let anyone who can type a customer's e-mail into a registration form
    /// take over their stand. Verification is what makes that claim mean anything.
    /// </remarks>
    public DateTime? EmailVerifiedAt { get; set; }

    /// <summary>
    /// TOTP secret, or null. OPT-IN here, unlike the operator's, where it is
    /// refused-without.
    /// </summary>
    /// <remarks>
    /// Mandatory second factor on a customer account would be security theatre
    /// with a cost: people who cannot complete it do not enrol, they telephone. The
    /// operator account is one account held by staff guarding the whole fleet, and
    /// can carry that requirement. A customer account reaches one stand they
    /// already own, and the honest control there is that it is offered, plainly,
    /// and enforced properly once switched on.
    /// </remarks>
    public byte[]? TotpSecret { get; set; }

    /// <summary>Replay guard: a code is single-use, exactly as for the operator.</summary>
    public long? LastTotpStep { get; set; }

    public int FailedAttempts { get; set; }

    /// <summary>
    /// Checked BEFORE the password is verified, for both reasons the operator
    /// account gives: it stops the response distinguishing "wrong password" from
    /// "right password, locked", and it stops a locked account spending CPU on
    /// bcrypt.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime? LastSignedInAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A signed-in customer session. Opaque token, SHA-256 of it stored, looked up on
/// every request — the same shape as <see cref="Auth.OperatorSession"/> and for the
/// same reason: revocation has to actually work, and a stateless JWT can only be
/// revoked by adding the database lookup you were avoiding.
/// </summary>
public class CustomerSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerAccountId { get; set; }

    public CustomerAccount? Account { get; set; }

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

/// <summary>What a one-shot link is for.</summary>
public enum CustomerTokenKind
{
    /// <summary>Proves the address at registration.</summary>
    VerifyEmail,

    /// <summary>Lets a password be set without knowing the old one.</summary>
    ResetPassword,
}

/// <summary>
/// A single-use link mailed to an address.
///
/// <para>
/// Stored as a hash, like a session, and for a sharper reason: this one is a
/// password equivalent that travels through e-mail and lands in a mailbox,
/// a mail log and possibly a corporate archive. Whoever can read the registry
/// must not be able to mint a sign-in from it.
/// </para>
///
/// <para>
/// <see cref="ConsumedAt"/> rather than deleting the row on use: "this link was
/// already used" is a different answer from "this link never existed", and during
/// an incident the difference is the whole investigation. Expired and consumed
/// rows are swept, not relied upon.
/// </para>
/// </summary>
public class CustomerToken
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerAccountId { get; set; }

    public CustomerAccount? Account { get; set; }

    public CustomerTokenKind Kind { get; set; }

    [Required]
    [MaxLength(32)]
    public byte[] TokenHash { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }
}

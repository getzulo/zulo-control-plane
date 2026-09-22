namespace ZuloOne.ControlPlane.Portal;

/// <summary>
/// The rate-limit policy guarding the portal's anonymous endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Named here rather than as a literal in each attribute because a typo in
/// <c>[EnableRateLimiting("portl-auth")]</c> does not fail to compile, does not
/// throw at start-up, and does not appear in any log — it simply means that one
/// endpoint has no rate limit. Registration and password reset are exactly the
/// endpoints where that matters.
/// </para>
/// <para>
/// A more generous budget than <c>operator-login</c>'s five a minute: this window
/// covers registration, verification and reset as well as sign-in, a real person
/// legitimately touches several in a row, and an office behind one address shares
/// the partition. The control that does not share a partition is the per-account
/// lockout, which is why that one is the security boundary and this is protection
/// for the CPU bcrypt would otherwise burn.
/// </para>
/// </remarks>
public static class PortalRateLimit
{
    public const string Policy = "portal-auth";
}

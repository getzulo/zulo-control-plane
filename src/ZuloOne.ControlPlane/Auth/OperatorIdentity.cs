using System.Security.Claims;

namespace ZuloOne.ControlPlane.Auth;

/// <summary>
/// Who is doing this, for the record.
/// </summary>
/// <remarks>
/// <para>
/// One helper because there were three spellings of it across eight call sites,
/// and they did not agree. Under Cloudflare Access the address arrives as the
/// claim <c>email</c> — <c>MapInboundClaims</c> is off in
/// <see cref="AuthSetup"/>, deliberately — so the call sites reaching for
/// <see cref="ClaimTypes.Email"/> recorded nothing at all. The Releases row for
/// 2026.9.5 has a null promoter for exactly that reason, and so do two of the
/// four most recent jobs.
/// </para>
///
/// <para>
/// Both spellings are tried, in that order, because break-glass sessions issue
/// the mapped URI (<see cref="OperatorSessionHandler"/>) while Access issues the
/// short name. A row that cannot say who is a row that answers "why is the fleet
/// on this version" with a shrug.
/// </para>
/// </remarks>
public static class OperatorIdentity
{
    /// <summary>The signed-in operator's address, or null when there is genuinely none.</summary>
    public static string? Of(ClaimsPrincipal? user) =>
        user?.FindFirst("email")?.Value
        ?? user?.FindFirst(ClaimTypes.Email)?.Value
        ?? user?.Identity?.Name;

    /// <summary>The same, for a log line that must not read "  did X".</summary>
    public static string Describe(ClaimsPrincipal? user) => Of(user) ?? "unknown";
}

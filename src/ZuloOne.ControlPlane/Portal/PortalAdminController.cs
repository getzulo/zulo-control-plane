using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Portal;

public record GrantMemberRequest(string Email, string? Role);

/// <summary>
/// The operator's side of portal membership: granting a customer access to a
/// stand, and seeing who already has it.
/// </summary>
/// <remarks>
/// <para>
/// Exists because self-claim cannot cover everything. A stand claims itself to
/// whoever proves the address in <see cref="Tenant.AdminEmail"/> — which handles
/// the ordinary case and nothing else. A stand provisioned against an internal
/// address, or against a typo, or for a company whose signatory is not the person
/// who will administer it, has no owner and no way to acquire one. That is a dead
/// end a customer cannot escape and only an operator can open.
/// </para>
/// <para>
/// A separate controller from <c>TenantsController</c> purely to keep this change
/// out of a file somebody else is working in. It carries no <c>[Authorize]</c> of
/// its own, so the fallback policy applies — Cloudflare Access or break-glass,
/// exactly like every other operator endpoint.
/// </para>
/// </remarks>
[ApiController]
[Route("api/tenants/{id:guid}/members")]
[Produces("application/json")]
public class PortalAdminController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly ILogger<PortalAdminController> _logger;

    public PortalAdminController(ControlPlaneDbContext db, ILogger<PortalAdminController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid id, CancellationToken ct)
    {
        var members = await _db.TenantMemberships
            .AsNoTracking()
            .Where(m => m.TenantId == id)
            .Include(m => m.Account)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return Ok(new
        {
            members = members.Select(m => new
            {
                id = m.Id,
                email = m.Account?.Email,
                displayName = m.Account?.DisplayName,
                role = m.Role.ToString(),
                // Whether they can actually sign in, which is a different question
                // from whether a grant exists. An unverified account holding an
                // Owner row looks like access and is not.
                verified = m.Account?.EmailVerifiedAt is not null,
                grantedBy = m.GrantedBy,
                since = m.CreatedAt,
            }),
        });
    }

    /// <summary>
    /// Gives a customer account access to this stand.
    /// </summary>
    /// <remarks>
    /// The account must already exist. Creating one from here would mean minting a
    /// credential for an address nobody has proved, and then either mailing it —
    /// turning the panel into a way to send sign-up links to arbitrary addresses —
    /// or not mailing it, leaving a password that exists nowhere. The customer
    /// signs up; the operator grants.
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Grant(Guid id, [FromBody] GrantMemberRequest request, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found", id });

        var email = request?.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email)) return BadRequest(new { error = "Which address?" });

        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (account is null)
        {
            return NotFound(new
            {
                error = $"No portal account uses {email}. Ask them to sign up at the portal first, then grant here.",
            });
        }

        var role = string.Equals(request!.Role, "member", StringComparison.OrdinalIgnoreCase)
            ? MembershipRole.Member
            // Owner is the default HERE, the opposite of the customer-facing
            // endpoint. An operator granting access is almost always opening a
            // stand that has no owner at all — which is the only situation this
            // endpoint exists for.
            : MembershipRole.Owner;

        var existing = await _db.TenantMemberships
            .FirstOrDefaultAsync(m => m.TenantId == id && m.CustomerAccountId == account.Id, ct);

        if (existing is not null)
        {
            existing.Role = role;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Operator {Operator} set {Email} to {Role} on tenant {Slug}",
                OperatorIdentity.Of(User), email, role, tenant.Slug);
            return Ok(new { updated = true, email, role = role.ToString(), verified = account.EmailVerifiedAt is not null });
        }

        _db.TenantMemberships.Add(new TenantMembership
        {
            TenantId = id,
            CustomerAccountId = account.Id,
            Role = role,
            GrantedBy = $"operator: {OperatorIdentity.Of(User)}",
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Operator {Operator} granted {Email} {Role} access to tenant {Slug}",
            OperatorIdentity.Of(User), email, role, tenant.Slug);

        return Ok(new
        {
            granted = true,
            email,
            role = role.ToString(),
            verified = account.EmailVerifiedAt is not null,
            note = account.EmailVerifiedAt is null
                ? "That account has not confirmed its address yet, so it cannot sign in until it does."
                : null,
        });
    }

    [HttpDelete("{membershipId:guid}")]
    public async Task<IActionResult> Revoke(Guid id, Guid membershipId, CancellationToken ct)
    {
        var membership = await _db.TenantMemberships
            .Include(m => m.Account)
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.TenantId == id, ct);
        if (membership is null) return NotFound(new { error = "No such member of this tenant." });

        // No last-owner guard here, unlike the customer's own endpoint. An operator
        // removing the final owner is a deliberate act — revoking a former
        // customer's access is exactly that — and this endpoint is also the only
        // way back from it.
        _db.TenantMemberships.Remove(membership);
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Operator {Operator} revoked {Email}'s access to tenant {TenantId}",
            OperatorIdentity.Of(User), membership.Account?.Email ?? "(unknown)", id);

        return Ok(new { revoked = true });
    }
}

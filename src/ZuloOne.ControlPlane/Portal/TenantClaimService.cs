using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Portal;

/// <summary>
/// Gives a verified account the stands whose administrator address is its own.
/// </summary>
/// <remarks>
/// <para>
/// This is how anybody gets a first membership without an operator doing it by
/// hand: provisioning already records <see cref="Tenant.AdminEmail"/>, the person
/// at that address is by definition the one the stand was built for, and
/// verification has proved they read that mailbox.
/// </para>
/// <para>
/// Run at verification AND at every sign-in, which is not belt-and-braces. A
/// customer commonly registers before their stand is provisioned — from the demo
/// they were given, or because sales told them to — and a claim that only ran at
/// verification would leave them staring at an empty portal for ever, with no
/// action available to them that would fix it.
/// </para>
/// </remarks>
public sealed class TenantClaimService
{
    private readonly ControlPlaneDbContext _db;
    private readonly PortalSettings _settings;
    private readonly ILogger<TenantClaimService> _logger;

    public TenantClaimService(
        ControlPlaneDbContext db, IOptions<PortalSettings> settings, ILogger<TenantClaimService> logger)
    {
        _db = db;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Returns how many stands were newly claimed.</summary>
    public async Task<int> ClaimAsync(CustomerAccount account, CancellationToken ct = default)
    {
        if (!_settings.ClaimByAdminEmail) return 0;

        // Unverified claims nothing. Without this line the whole scheme inverts:
        // typing a customer's address into a registration form would hand over
        // their stand.
        if (account.EmailVerifiedAt is null) return 0;

        var candidates = await _db.Tenants
            .Where(t => t.AdminEmail != null
                        && t.AdminEmail.ToLower() == account.Email
                        // A demo is pooled infrastructure on a timer, handed to a
                        // visitor. Claiming it would litter the portal with rows
                        // that vanish, and the portal refuses to act on one anyway.
                        && t.Demo == null
                        && t.Status != TenantStatus.Deleting)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var already = await _db.TenantMemberships
            .Where(m => m.CustomerAccountId == account.Id)
            .Select(m => m.TenantId)
            .ToListAsync(ct);

        var claimed = 0;
        foreach (var tenant in candidates)
        {
            if (already.Contains(tenant.Id)) continue;

            // Owner only when nobody owns it yet. A second address that happens to
            // match later must not silently become a co-owner of a stand somebody
            // is already running — that grant is the existing owner's to make.
            var hasOwner = await _db.TenantMemberships
                .AnyAsync(m => m.TenantId == tenant.Id && m.Role == MembershipRole.Owner, ct);

            _db.TenantMemberships.Add(new TenantMembership
            {
                CustomerAccountId = account.Id,
                TenantId = tenant.Id,
                Role = hasOwner ? MembershipRole.Member : MembershipRole.Owner,
                GrantedBy = "self-claimed: verified address matches the stand's administrator",
            });
            claimed++;

            _logger.LogInformation(
                "Portal account {Email} claimed tenant {Slug} as {Role}",
                account.Email, tenant.Slug, hasOwner ? "member" : "owner");
        }

        if (claimed > 0) await _db.SaveChangesAsync(ct);
        return claimed;
    }
}

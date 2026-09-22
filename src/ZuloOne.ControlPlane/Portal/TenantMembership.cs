using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Portal;

/// <summary>What a member may do on a stand, before plan and status are consulted.</summary>
public enum MembershipRole
{
    /// <summary>
    /// The person the subscription belongs to. Can act on the container and can
    /// grant and revoke other members.
    /// </summary>
    Owner,

    /// <summary>
    /// Someone the owner let in — an accountant, an IT contractor. Can look, and
    /// can restore a user's access, which is the everyday reason to be here.
    /// Cannot stop the stand and cannot change who else is a member.
    /// </summary>
    Member,
}

/// <summary>
/// One customer's access to one stand.
/// </summary>
/// <remarks>
/// <para>
/// A join table rather than an owner column on <see cref="Registry.Tenant"/>,
/// for two reasons. A real customer is rarely one person — the owner signs the
/// contract, the bookkeeper resets passwords, someone's IT contractor reads the
/// logs — and a single column forces them to share one login, which is how
/// credentials end up in a group chat. And <c>Tenant</c> is the operator's row
/// about infrastructure; whose account can see it is a different subject that
/// should not be able to break a provisioning query by being edited.
/// </para>
/// <para>
/// A foreign key to Tenant WITH cascade, unlike Jobs and Snapshots, which
/// deliberately have none. Those are history and are supposed to outlive their
/// tenant. This is access. A membership row pointing at a tenant that no longer
/// exists is not a historical record, it is a dangling grant — and the portal
/// would list it as a stand the customer owns, then fail opaquely on every action.
/// </para>
/// </remarks>
public class TenantMembership
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerAccountId { get; set; }

    public CustomerAccount? Account { get; set; }

    public Guid TenantId { get; set; }

    public Registry.Tenant? Tenant { get; set; }

    public MembershipRole Role { get; set; } = MembershipRole.Member;

    /// <summary>
    /// How this grant came about — self-claimed on a verified address, granted by
    /// another member, or created by an operator. Kept because "who let them in"
    /// is the first question asked about any access nobody expected.
    /// </summary>
    [MaxLength(200)]
    public string? GrantedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

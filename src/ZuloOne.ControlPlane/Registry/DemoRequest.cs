using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Registry;

/// <summary>How far a demo request got.</summary>
public enum DemoRequestState
{
    /// <summary>Accepted, waiting for a pooled workspace to become free.</summary>
    Queued,

    /// <summary>A workspace was claimed for it; <see cref="DemoRequest.TenantSlug"/> says which.</summary>
    Ready,

    /// <summary>Waited too long without a workspace. Terminal.</summary>
    Expired,

    /// <summary>Refused by policy — quota, duplicate address, fleet at its ceiling.</summary>
    Rejected,

    /// <summary>Something broke while serving it; <see cref="DemoRequest.Note"/> says what.</summary>
    Failed,
}

/// <summary>
/// Somebody asked for a demo workspace from the public site.
/// </summary>
/// <remarks>
/// <para>
/// Kept as its own table rather than as columns on <see cref="Tenant"/> because
/// most requests never become a tenant — they are queued, refused by quota, or
/// expire waiting. Those outcomes are exactly what has to be visible when
/// somebody asks why a campaign produced no trials, and a row that only exists
/// when provisioning succeeded cannot answer that.
/// </para>
/// <para>
/// It is also a sales lead: the address is the only thing the visitor gives us.
/// Retention therefore follows the privacy policy, not the tenant's 24 hours.
/// </para>
/// </remarks>
public class DemoRequest
{
    /// <summary>
    /// Minted by the caller, not the server.
    /// </summary>
    /// <remarks>
    /// This is what makes the request retryable. A dropped response on a POST
    /// would otherwise leave the visitor with no workspace and no way to ask
    /// again without burning a second one from the pool; with a caller-supplied
    /// id, the retry is recognised as the same request and returns the same
    /// answer.
    /// </remarks>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Where to reach whoever asked. Also the per-address quota key.</summary>
    [Required]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Company { get; set; }

    /// <summary>Which language the site was in — worth knowing before a reply is written.</summary>
    [MaxLength(16)]
    public string? Locale { get; set; }

    /// <summary>Caller's address, relayed by the bridge. Sized for IPv6.</summary>
    [MaxLength(45)]
    public string? SourceIp { get; set; }

    [MaxLength(2)]
    public string? Country { get; set; }

    public DemoRequestState State { get; set; } = DemoRequestState.Queued;

    /// <summary>The workspace handed over, while it still exists.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The slug, kept separately so the history still reads after the tenant is
    /// reaped and <see cref="TenantId"/> points at nothing.
    /// </summary>
    [MaxLength(63)]
    public string? TenantSlug { get; set; }

    /// <summary>Why it was rejected or what failed. Null when nothing went wrong.</summary>
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ReadyAt { get; set; }
}

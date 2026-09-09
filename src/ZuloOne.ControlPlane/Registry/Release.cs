using System.ComponentModel.DataAnnotations;

namespace ZuloOne.ControlPlane.Registry;

/// <summary>
/// A build somebody decided to call a release.
/// </summary>
/// <remarks>
/// <para>
/// The registry holds the image; this holds the DECISION — who promoted it, when,
/// from which build, and why. A tag in a registry cannot answer any of that, and
/// "why is the fleet on 2026.9.5" is exactly the question asked six weeks later.
/// </para>
///
/// <para>
/// <b>Promotion adds a NAME, it does not copy anything.</b> The release tag and
/// the build tag point at one manifest with one digest, which is the property
/// that makes a release trustworthy: the bytes that were tested are the bytes
/// that ship, with no rebuild in between. It also means the two tags live and die
/// together — deleting either removes the image from under both, which is why
/// <c>ImagesController.Delete</c> judges the whole set of names on a manifest
/// rather than the one that was clicked.
/// </para>
/// </remarks>
public class Release
{
    [Key]
    [MaxLength(50)]
    public string Version { get; set; } = string.Empty;

    /// <summary>The build tag this was promoted from — <c>2026.0.34</c>.</summary>
    [MaxLength(200)]
    public string SourceTag { get; set; } = string.Empty;

    /// <summary>
    /// The manifest digest, recorded at promotion.
    /// </summary>
    /// <remarks>
    /// The one field that can prove, later, that the release tag still points at
    /// what was promoted. A registry tag is mutable by anything holding a push
    /// token; a digest written down here is not.
    /// </remarks>
    [MaxLength(100)]
    public string Digest { get; set; } = string.Empty;

    /// <summary>Which repository — the panel and the platform are released separately.</summary>
    [MaxLength(200)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>What changed, in the words of whoever promoted it.</summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }

    [MaxLength(200)]
    public string? PromotedBy { get; set; }

    public DateTime PromotedAt { get; set; } = DateTime.UtcNow;
}

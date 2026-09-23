namespace ZuloOne.ControlPlane.Registry;

/// <summary>
/// What counts as a release tag, and which of two is newer.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>ImagesController</c> when the customer portal needed the
/// same two answers. Copying them would have been three lines and a latent bug:
/// the operator's list and the customer's "you are behind" banner would disagree
/// the first time either definition moved, and the customer-visible one is the
/// one nobody would check.
/// </para>
/// <para>
/// A release is CalVer with a non-zero month. <c>2026.0.&lt;run&gt;</c> is the
/// sentinel for "not a release" and <c>sha-&lt;commit&gt;</c> is a build
/// artefact; neither may be offered to a customer as a version to move to.
/// </para>
/// </remarks>
public static class ReleaseVersion
{
    /// <summary>Newest first when used with <c>OrderByDescending</c>.</summary>
    public static readonly IComparer<string> CalVer = Comparer<string>.Create((a, b) =>
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < 3; i++)
        {
            var c = int.Parse(x[i]).CompareTo(int.Parse(y[i]));
            if (c != 0) return c;
        }
        return 0;
    });

    public static bool IsRelease(string tag)
    {
        var parts = tag.Split('.');
        return parts.Length == 3
            && int.TryParse(parts[0], out var year) && year > 2000
            && int.TryParse(parts[1], out var month) && month is >= 1 and <= 12
            && int.TryParse(parts[2], out _);
    }

    /// <summary>
    /// Is <paramref name="candidate"/> strictly newer than <paramref name="current"/>?
    /// </summary>
    /// <remarks>
    /// False whenever either side is not a release tag. A tenant pinned to a
    /// build — which happens during an investigation — must not be told it is
    /// "behind" and offered a move that would undo the pin.
    /// </remarks>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(current)) return false;
        if (!IsRelease(candidate) || !IsRelease(current)) return false;
        return CalVer.Compare(candidate, current) > 0;
    }

    /// <summary>
    /// Splits <c>host:port/repository:tag</c> into its registry and repository.
    /// </summary>
    /// <remarks>
    /// Shared with the operator panel for a reason that bit immediately: the
    /// portal first did `image.Split(':')[0]`, which on
    /// <c>10.10.1.20:5000/zuloone-core:2026.9.0</c> returns the REGISTRY HOST.
    /// Releases were then filtered by a repository that matches nothing, and the
    /// customer was told there is no release to move to — a wrong answer that
    /// looks like a true one.
    /// </remarks>
    public static (string? Registry, string Repository) SplitImage(string image)
    {
        var withoutTag = image.Contains(':') && image.LastIndexOf(':') > image.LastIndexOf('/')
            ? image[..image.LastIndexOf(':')]
            : image;
        var slash = withoutTag.IndexOf('/');
        // A registry host is recognisable by carrying a port or a dot; without one
        // this is a Docker Hub name and there is no local registry to query.
        if (slash < 0) return (null, withoutTag);
        var head = withoutTag[..slash];
        return head.Contains(':') || head.Contains('.') ? (head, withoutTag[(slash + 1)..]) : (null, withoutTag);
    }
}

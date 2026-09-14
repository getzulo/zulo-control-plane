namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Whether a running container is the image the tenant is pinned to.
/// </summary>
/// <remarks>
/// A release is a new name on an existing manifest — promote copies no bytes —
/// so <c>GET /health</c> reports the AssemblyVersion the binary was compiled
/// with, not the tag the operator pinned. Comparing those two is why the panel
/// called every promoted tenant "different". The daemon's image name or digest
/// is the comparison that matches what "recreate on this pin" would start.
/// </remarks>
public static class ImagePin
{
    public static bool Matches(string? pinned, string? runningName, string? runningId, string? pinnedId)
    {
        if (string.IsNullOrWhiteSpace(pinned)) return false;

        if (!string.IsNullOrWhiteSpace(runningName)
            && string.Equals(pinned.Trim(), runningName.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var a = NormalizeId(runningId);
        var b = NormalizeId(pinnedId);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        var value = id.Trim();
        const string prefix = "sha256:";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }
}

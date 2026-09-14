namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// The per-tenant customization model the platform seeds on first boot
/// (layer 4). Same row everywhere; the display name is the tenant in
/// PascalCase, or Local on an anonymous stand.
/// </summary>
/// <remarks>
/// It is not a product. A workspace export of a local Core stand once
/// committed <c>Local/</c>, CI put <c>Local=1.0.0</c> on the distribution
/// label, and the catalogue — which unions every tag — grew a package
/// nobody created and nobody has installed.
/// </remarks>
public static class StandModel
{
    public const string MetaId = "7e2c1f0a-9b4d-4e6a-8c3f-1d5a7b9e2c40";

    public static bool IsMetaId(string? metaId) =>
        Guid.TryParse(metaId, out var id)
        && id == Guid.Parse(MetaId);

    /// <summary>Historical folder / label names of that same row.</summary>
    public static bool IsShippedName(string? name) =>
        string.Equals(name, "Local", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Tenant", StringComparison.OrdinalIgnoreCase);
}

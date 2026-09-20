using System.Security.Cryptography;

namespace ZuloOne.ControlPlane.Provisioning.Demo;

/// <summary>
/// Names for pooled demo workspaces.
/// </summary>
/// <remarks>
/// The <c>demo-</c> prefix is not decoration. <c>demo</c> itself is a reserved
/// slug, but <see cref="ReservedSlugs.IsReserved"/> matches whole names, so the
/// prefix is free — which means an operator could hand a customer
/// <c>demo-acme</c> and collide with the pool's namespace. The prefix is
/// therefore claimed explicitly in <see cref="TenantProvisioner.RegisterAsync"/>.
/// </remarks>
public static class DemoSlug
{
    public const string Prefix = "demo-";

    /// <summary>
    /// The same unambiguous alphabet <c>RestoreJobHandler</c> uses: no 0/O, no
    /// 1/l/i. This name is read off a web page and typed into a browser by a
    /// stranger, and that is exactly how it goes wrong.
    /// </summary>
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private const int Length = 6;

    /// <summary>A fresh pool name, e.g. <c>demo-k7m2xq</c>.</summary>
    public static string Mint()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return Prefix + new string(chars);
    }

    /// <summary>Whether a slug belongs to the demo pool's namespace.</summary>
    public static bool IsDemoSlug(string? slug) =>
        slug is not null && slug.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

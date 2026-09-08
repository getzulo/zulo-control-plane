using System.Collections.Frozen;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Subdomains a customer may not take.
///
/// The wildcard DNS record answers for EVERY name under the root domain, so DNS
/// reserves nothing — it cannot. If a customer is allowed to choose the slug
/// <c>admin</c>, they get <c>admin.zulo.one</c>, and if they choose <c>cp</c> they
/// take the address of the control plane itself. This list is the only thing
/// standing between a signup form and that outcome, which is why it lives in code
/// with the baseline compiled in rather than in a document or a config file
/// somebody can empty by accident.
///
/// <para>
/// Ops can EXTEND the list through <c>Fleet:AdditionalReservedSlugs</c> — the set
/// grows as products and environments are added — but nothing can shrink the
/// baseline below.
/// </para>
///
/// <para>
/// Adding a name here does not disturb a tenant already using it: the check runs
/// at provisioning time only. Reclaiming such a slug is a rename, deliberately not
/// something this class does silently.
/// </para>
/// </summary>
public static class ReservedSlugs
{
    /// <summary>
    /// Compiled in, and not removable through configuration. Grouped by why each
    /// name is dangerous, because the reason is what tells you whether a new
    /// candidate belongs.
    /// </summary>
    private static readonly FrozenSet<string> Baseline = new[]
    {
        // Infrastructure and anything an operator would reasonably expect to own.
        "admin", "administrator", "api", "app", "www", "cp", "panel", "console",
        "dashboard", "manage", "management", "portal", "gateway", "proxy", "edge",
        "registry", "ci", "cd", "build", "runner", "cdn", "static", "assets",
        "media", "files", "download", "downloads", "backup", "backups",

        // Names resolvers and mail clients probe by convention. A tenant here does
        // not just take an address — it intercepts traffic aimed at the service.
        "mail", "email", "smtp", "imap", "pop", "pop3", "mx", "ns", "ns1", "ns2",
        "ns3", "ns4", "dns", "ftp", "sftp", "ssh", "vpn", "git", "svn",
        "autodiscover", "autoconfig", "_domainkey", "dmarc", "spf",

        // Environments. These read as trustworthy to a human, which is exactly the
        // problem: "staging.zulo.one" looks like ours no matter who owns it.
        "prod", "production", "live", "uat", "test", "testing", "qa", "stage",
        "staging", "dev", "develop", "development", "demo", "sandbox", "preview",
        "canary", "beta", "alpha", "next", "internal", "local", "localhost",

        // Product and brand. Impersonation surface.
        "zulo", "zuloone", "zulo-one", "getzulo", "erp", "core", "platform",
        "docs", "doc", "documentation", "wiki", "help", "support", "kb",
        "status", "health", "blog", "news", "about", "contact", "legal",
        "privacy", "terms", "pricing", "shop", "store", "partners", "partner",

        // Anything that could be mistaken for authentication or money.
        "login", "logout", "signin", "signup", "register", "auth", "oauth", "sso",
        "id", "identity", "account", "accounts", "profile", "billing", "invoice",
        "invoices", "payment", "payments", "checkout", "subscribe", "subscription",
        "secure", "security", "verify", "verification", "token", "session",

        // RFC 2142 and abuse-desk addresses. Owning these is how a domain answers
        // for itself; a tenant holding one can intercept the reply.
        "abuse", "postmaster", "hostmaster", "webmaster", "noc", "root", "sysadmin",
        "noreply", "no-reply", "donotreply", "info", "sales", "marketing",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extra names from <c>Fleet:AdditionalReservedSlugs</c>. Replaced wholesale on
    /// configure; the baseline above is unioned in on every lookup, so a bad value
    /// here can only over-reserve, never under-reserve.
    /// </summary>
    private static FrozenSet<string> _additional = FrozenSet<string>.Empty;

    /// <summary>Called once at startup from the bound <see cref="FleetSettings"/>.</summary>
    public static void Configure(IEnumerable<string>? additional) =>
        _additional = (additional ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every reserved name, baseline plus configured, for diagnostics.</summary>
    public static IReadOnlyCollection<string> All =>
        Baseline.Concat(_additional).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// True when this slug must not be handed to a customer.
    ///
    /// The <c>xn--</c> test is not decoration. That prefix marks a punycode label,
    /// and a valid one such as <c>xn--dmin-7na</c> renders in the address bar as
    /// "аdmin" with a Cyrillic а — it passes the ASCII slug pattern, is absent from
    /// every list anyone would think to write, and is indistinguishable from the
    /// real thing to a reader. Refuse the whole encoding: no legitimate tenant
    /// needs it, and allowing it would mean maintaining a homoglyph table forever.
    /// </summary>
    public static bool IsReserved(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return true;
        var s = slug.Trim();
        return s.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)
            || Baseline.Contains(s)
            || _additional.Contains(s);
    }
}

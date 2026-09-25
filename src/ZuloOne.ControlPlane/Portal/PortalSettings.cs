namespace ZuloOne.ControlPlane.Portal;

/// <summary>
/// Configuration for the customer portal. Bound from <c>Portal:</c>.
/// </summary>
public sealed class PortalSettings
{
    /// <summary>
    /// Whether the portal answers at all. DEFAULT FALSE, deliberately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This application is the operator's panel, and the panel is reached through
    /// Cloudflare Access. The portal is the one part of it that must be reachable
    /// by people who are not staff — which means somebody has to write a Cloudflare
    /// policy that bypasses Access for <c>/api/portal</c> and <c>/portal</c>, and
    /// nothing in this codebase can verify that they did it correctly.
    /// </para>
    /// <para>
    /// A default of true would mean an upgrade silently publishes a registration
    /// form on whatever hostname the panel happens to answer on. Off until asked
    /// makes turning it on the moment somebody thinks about the edge policy —
    /// which is the moment it needs thinking about. See <c>docs/Portal.md</c>.
    /// </para>
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Where the portal is reachable from a browser, e.g.
    /// <c>https://my.zulo.one</c>. Used to build the links that go in e-mail.
    /// </summary>
    /// <remarks>
    /// Taken from configuration rather than from the request's own Host header.
    /// A verification link built from an attacker-supplied Host is how account
    /// takeover by host header injection works: the mail goes to the real address,
    /// the link points at the attacker, and the token is handed over by the person
    /// who owns the mailbox.
    /// </remarks>
    public string? PublicUrl { get; set; }

    /// <summary>How long a signed-in session lasts.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>How long a verification link stays good.</summary>
    public int VerifyTokenHours { get; set; } = 48;

    /// <summary>
    /// How long a reset link stays good. Short on purpose: it is a password
    /// equivalent sitting in a mailbox.
    /// </summary>
    public int ResetTokenMinutes { get; set; } = 60;

    /// <summary>Failed sign-ins before the account is locked.</summary>
    public int LockoutThreshold { get; set; } = 8;

    public int LockoutMinutes { get; set; } = 15;

    /// <summary>
    /// Whether a newly verified address may take ownership of a stand whose
    /// <c>AdminEmail</c> matches it.
    /// </summary>
    /// <remarks>
    /// On by default because it is how anybody gets a first membership without an
    /// operator doing it by hand, and because the address has been proven by then.
    /// Switchable because a deployment that provisions stands with a shared
    /// internal address — <c>billing@</c>, say — would have one account claim the
    /// whole fleet, and there the grant must be manual.
    /// </remarks>
    public bool ClaimByAdminEmail { get; set; } = true;

    /// <summary>
    /// Shared with the getzulo.com cabinet. A directory sign-in presents this
    /// key and an address; a verified portal account with that address receives
    /// a portal session. Empty means the link is off.
    /// </summary>
    public string? CabinetKey { get; set; }

    /// <summary>
    /// Path of the page that finishes verification, appended to
    /// <see cref="PublicUrl"/>. <c>{locale}</c> is replaced with the account's
    /// language.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Configurable because the page that handles the link is NOT always this
    /// application. When the customer cabinet moved to getzulo.com the link had
    /// to become <c>/{locale}/cabinet/verify</c>; with the path hardcoded, every
    /// verification mail pointed at a 404 — and the only symptom would be
    /// customers who can register and never sign in.
    /// </para>
    /// <para>
    /// The default keeps the behaviour of the built-in SPA at <c>/portal</c>, so
    /// a deployment that does not use the cabinet changes nothing.
    /// </para>
    /// </remarks>
    public string VerifyPath { get; set; } = "/portal/verify";

    /// <summary>Path of the password-reset page. See <see cref="VerifyPath"/>.</summary>
    public string ResetPath { get; set; } = "/portal/reset";

    /// <summary>
    /// Language used in a link when the account has none recorded, and the
    /// fallback for anything not in <see cref="Locales"/>.
    /// </summary>
    public string DefaultLocale { get; set; } = "en";

    /// <summary>
    /// Languages that may appear in a mailed link.
    /// </summary>
    /// <remarks>
    /// An allow-list, not a format check. <c>CustomerAccount.Locale</c> is
    /// supplied at registration and is therefore attacker-influenced: without
    /// this, a sign-up carrying <c>../../somewhere</c> would mail ITSELF a link
    /// that leaves the cabinet — from our domain, in our template, with a valid
    /// token attached. Anything not listed falls back to
    /// <see cref="DefaultLocale"/>.
    /// </remarks>
    public string[] Locales { get; set; } = ["en", "ru", "ar", "uk"];
}

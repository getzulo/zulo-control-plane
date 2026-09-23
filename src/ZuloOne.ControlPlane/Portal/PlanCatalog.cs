using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Portal;

/// <summary>One thing a customer might try to do to their own stand.</summary>
public enum PortalCapability
{
    /// <summary>See that the stand exists, its state and its address.</summary>
    ViewTenant,

    /// <summary>Read its size and usage figures.</summary>
    ViewStats,

    /// <summary>Read its container log.</summary>
    ViewLogs,

    /// <summary>Set a new password for one of the stand's own users.</summary>
    ResetUserPassword,

    /// <summary>Restart the container.</summary>
    RestartTenant,

    /// <summary>Stop it, or start it again.</summary>
    StopStartTenant,

    /// <summary>Let another person in, or put them out.</summary>
    ManageMembers,
}

/// <summary>
/// Whether an action is allowed, and — when it is not — the sentence to show the
/// person who tried.
/// </summary>
/// <remarks>
/// The reason is not decoration. A bare 403 on "restart my own stand" reads as a
/// bug and generates the support ticket this portal exists to prevent; "your
/// subscription is suspended, so the stand is read-only until it is settled" is
/// self-service. Carrying the reason in the decision, rather than reconstructing
/// it at the call site, is what stops the two drifting apart.
/// </remarks>
public readonly record struct PortalDecision(bool Allowed, string? Reason)
{
    public static PortalDecision Yes { get; } = new(true, null);

    public static PortalDecision No(string reason) => new(false, reason);
}

/// <summary>
/// What a given subscription includes, exactly as published on the price list.
/// </summary>
/// <remarks>
/// <para>
/// Nullable almost everywhere on purpose. Enterprise is negotiated, so most of its
/// figures are genuinely "by agreement" and a number there would be an invention;
/// an unrecognised plan code has no published figures at all. Writing 0 or a
/// plausible default would put a promise on a customer's screen that no contract
/// backs, which is worse than an empty cell saying "talk to us".
/// </para>
/// <para>
/// These figures mirror <c>getzulo.com/src/messages/*.json</c> under
/// <c>pricing.tiers</c>. If the price list changes and this does not, the portal
/// tells customers something the site contradicts — so they change together.
/// </para>
/// </remarks>
public sealed record PlanFacts(
    string Code,
    string Name,
    int? OfficeUsers,
    int? FieldAgents,
    string? BackupCadence,
    int? RestoreDays,
    string? SupportResponse,
    string? Availability,
    bool SandboxIncluded);

/// <summary>
/// What each plan includes, and who may do what to a stand.
/// </summary>
/// <remarks>
/// <para>
/// The two halves are deliberately separate, because conflating them is the
/// mistake this class exists to avoid. <see cref="Describe"/> is the published
/// price list: seats, backup retention, support response. <see cref="Decide"/> is
/// the access gate: role, subscription state, demo.
/// </para>
/// <para>
/// The plan does NOT gate the buttons, and that is a decision rather than an
/// omission. The price list differentiates on seats, backup retention, support
/// speed, a sandbox stand and an availability target — not one of which is
/// "may restart the container". Gating restart behind Business would be a
/// restriction we never sold, invented in code, which is the same drift as
/// promising an entitlement we never sold, only harder to notice because it
/// only ever makes a customer's day worse.
/// </para>
/// <para>
/// What DOES gate the buttons is the subscription being live. An operator who
/// has not been paid moves the tenant to <see cref="TenantStatus.Suspended"/>,
/// and from that moment the portal is read-only. That is the licence check, it
/// runs off state that already existed, and it needs no separate billing flag
/// to fall out of step with reality.
/// </para>
/// </remarks>
public static class PlanCatalog
{
    public const string Start = "start";
    public const string Business = "business";
    public const string Enterprise = "enterprise";

    /// <summary>
    /// The published plans, keyed by the code stored in
    /// <see cref="Tenant.Plan"/>. Codes are lower-case; the column is free text
    /// and has been written by hand, so comparison is case-insensitive.
    /// </summary>
    private static readonly Dictionary<string, PlanFacts> Published = new(StringComparer.OrdinalIgnoreCase)
    {
        [Start] = new(
            Code: Start,
            Name: "Start",
            OfficeUsers: 10,
            FieldAgents: 15,
            BackupCadence: "nightly",
            RestoreDays: 14,
            SupportResponse: "next working day",
            Availability: null,
            SandboxIncluded: false),

        [Business] = new(
            Code: Business,
            Name: "Business",
            OfficeUsers: 40,
            FieldAgents: null,
            BackupCadence: "hourly",
            RestoreDays: 90,
            SupportResponse: "four-hour first response",
            Availability: "99.5%",
            SandboxIncluded: true),

        [Enterprise] = new(
            Code: Enterprise,
            Name: "Enterprise",
            OfficeUsers: null,
            FieldAgents: null,
            // Enterprise chooses its own backup window, so there is no single
            // cadence to state. Null, not a guess.
            BackupCadence: null,
            RestoreDays: null,
            SupportResponse: "named engineer",
            Availability: "99.9%",
            SandboxIncluded: true),
    };

    /// <summary>
    /// What this plan code includes. An unrecognised or missing code returns a
    /// record with no figures rather than falling back to the cheapest plan.
    /// </summary>
    /// <remarks>
    /// Falling back to Start would be the tempting default and is wrong twice
    /// over: it would tell an Enterprise customer whose plan code was mistyped
    /// that they have a 14-day restore window, and it would make a typo
    /// indistinguishable from a deliberate choice when somebody audits the fleet.
    /// </remarks>
    public static PlanFacts Describe(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && Published.TryGetValue(code.Trim(), out var facts))
            return facts;

        return new PlanFacts(
            Code: code?.Trim() ?? string.Empty,
            // Named for what it is. "Unknown plan" on a screen is a prompt to ask,
            // which is the correct outcome — somebody has to look at the row.
            Name: string.IsNullOrWhiteSpace(code) ? "Not set" : code.Trim(),
            OfficeUsers: null,
            FieldAgents: null,
            BackupCadence: null,
            RestoreDays: null,
            SupportResponse: null,
            Availability: null,
            SandboxIncluded: false);
    }

    /// <summary>Every published plan, for a chooser or a comparison table.</summary>
    public static IReadOnlyCollection<PlanFacts> All => Published.Values;

    /// <summary>
    /// May this member do this to this stand, right now?
    /// </summary>
    /// <remarks>
    /// Reads are always allowed: membership is how you got here, and a member who
    /// may not see the state of their own stand has no reason to have an account.
    /// Everything that changes something is gated, in this order, first refusal
    /// winning — and each refusal names itself, because the caller shows it.
    /// </remarks>
    public static PortalDecision Decide(
        PortalCapability capability, Tenant tenant, MembershipRole role, DateTime utcNow)
    {
        // Reading is never refused. Kept first and unconditional so that a future
        // gate added below cannot accidentally lock a customer out of the page
        // that would explain why they are locked out.
        if (capability is PortalCapability.ViewTenant or PortalCapability.ViewStats or PortalCapability.ViewLogs)
            return PortalDecision.Yes;

        // Suspended means two different things, and conflating them stranded the
        // customer.
        //
        // An operator sets it when a subscription is not settled — that is the
        // licence gate below. But `Stop` sets it too, at the customer's own
        // request, and records `StoppedByCustomer`. Without this branch the
        // capability map answered "read-only, settle your subscription" to
        // somebody who had pressed Stop a minute earlier: the Start button never
        // appeared and the stand could not be brought back from the portal at
        // all — while `Start` itself was written for exactly this case and even
        // gives an operator-suspension its own distinct 403.
        //
        // Only starting is restored. The rest stays read-only, because the stand
        // really is down and a restart or a password reset against it would fail.
        if (tenant.Status == TenantStatus.Suspended
            && tenant.StoppedByCustomer
            && capability is PortalCapability.StopStartTenant)
        {
            return role == MembershipRole.Owner
                ? PortalDecision.Yes
                : PortalDecision.No("Only the stand's owner can do this.");
        }

        // The licence gate. Suspended is what an operator sets when a subscription
        // is not settled, so this is the one place the portal enforces payment —
        // off state that already exists rather than a billing flag of its own.
        if (tenant.Status == TenantStatus.Suspended)
            return PortalDecision.No(
                "This stand is suspended, so it is read-only here. Settling the subscription restores it.");

        // A term that has run out. Distinct from Suspended because nobody had to
        // act for it to happen, so the sentence has to say something different.
        if (tenant.ExpiresAt is { } expires && expires <= utcNow)
            return PortalDecision.No("This stand's term has ended, so it is read-only here.");

        // Provisioning, Failed, Deleting: there is no running stand to act on, and
        // a button that reports success against one of these would be lying.
        if (tenant.Status != TenantStatus.Active)
            return PortalDecision.No($"This stand is {tenant.Status.ToString().ToLowerInvariant()}, so it cannot be changed from here.");

        // A demo is pooled infrastructure on a timer, handed to a visitor who has
        // not signed anything. Letting it be stopped and started is a free way to
        // churn shared capacity, and the 24-hour clock makes the action pointless
        // anyway — it will be gone before it matters.
        if (tenant.Demo is not null)
            return PortalDecision.No("A demo workspace is read-only here — it is removed automatically when its time is up.");

        // Role last, so that the answer a Member gets is about the stand's state
        // when that is the real obstacle, and about their role only when it is.
        if (capability is PortalCapability.StopStartTenant or PortalCapability.ManageMembers
            && role != MembershipRole.Owner)
        {
            return PortalDecision.No("Only the stand's owner can do this.");
        }

        return PortalDecision.Yes;
    }
}

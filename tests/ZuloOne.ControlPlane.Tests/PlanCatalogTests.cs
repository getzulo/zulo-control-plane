using ZuloOne.ControlPlane.Portal;
using ZuloOne.ControlPlane.Registry;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

/// <summary>
/// The licence gate and the published price list, which are deliberately two
/// different things — see <see cref="PlanCatalog"/>.
/// </summary>
public sealed class PlanCatalogTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static Tenant Stand(
        TenantStatus status = TenantStatus.Active,
        string? plan = PlanCatalog.Business,
        TenantDemo? demo = null,
        DateTime? expires = null) => new()
        {
            Slug = "romashka",
            Status = status,
            Plan = plan,
            Demo = demo,
            ExpiresAt = expires,
        };

    // ------------------------------------------------------- the price list ---

    [Theory]
    [InlineData(PlanCatalog.Start, "Start", 10, 14, false)]
    [InlineData(PlanCatalog.Business, "Business", 40, 90, true)]
    public void Published_plans_carry_their_published_figures(
        string code, string name, int users, int restoreDays, bool sandbox)
    {
        var facts = PlanCatalog.Describe(code);

        Assert.Equal(name, facts.Name);
        Assert.Equal(users, facts.OfficeUsers);
        Assert.Equal(restoreDays, facts.RestoreDays);
        Assert.Equal(sandbox, facts.SandboxIncluded);
    }

    /// <summary>The column is free text written by hand; casing must not matter.</summary>
    [Theory]
    [InlineData("Business")]
    [InlineData("BUSINESS")]
    [InlineData("  business  ")]
    public void Plan_codes_are_matched_case_and_space_insensitively(string code)
    {
        Assert.Equal("Business", PlanCatalog.Describe(code).Name);
    }

    /// <summary>
    /// Enterprise is negotiated, so most of its figures must be absent rather
    /// than invented. A number here would be a promise no contract backs.
    /// </summary>
    [Fact]
    public void Enterprise_states_no_figure_it_has_not_published()
    {
        var facts = PlanCatalog.Describe(PlanCatalog.Enterprise);

        Assert.Null(facts.OfficeUsers);
        Assert.Null(facts.RestoreDays);
        Assert.Null(facts.BackupCadence);
        Assert.Equal("99.9%", facts.Availability);
    }

    /// <summary>
    /// An unrecognised code must NOT fall back to the cheapest plan. Doing so
    /// would tell an Enterprise customer whose code was mistyped that they have a
    /// 14-day restore window, and make a typo indistinguishable from a choice.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("premium-plus")]
    public void An_unknown_plan_promises_nothing(string? code)
    {
        var facts = PlanCatalog.Describe(code);

        Assert.Null(facts.OfficeUsers);
        Assert.Null(facts.RestoreDays);
        Assert.Null(facts.SupportResponse);
        Assert.False(facts.SandboxIncluded);
    }

    // -------------------------------------------------------- the access gate ---

    [Theory]
    [InlineData(PortalCapability.ViewTenant)]
    [InlineData(PortalCapability.ViewStats)]
    [InlineData(PortalCapability.ViewLogs)]
    public void Reading_is_never_refused(PortalCapability capability)
    {
        // Every shape that refuses a write, at once: suspended, expired, a demo,
        // and the least-privileged role.
        var hopeless = Stand(
            status: TenantStatus.Suspended,
            demo: TenantDemo.Claimed,
            expires: Now.AddDays(-30));

        Assert.True(PlanCatalog.Decide(capability, hopeless, MembershipRole.Member, Now).Allowed);
    }

    /// <summary>
    /// The licence gate. Suspended is what an operator sets when a subscription is
    /// not settled, so this one assertion is the whole payment enforcement.
    /// </summary>
    [Theory]
    [InlineData(PortalCapability.ResetUserPassword)]
    [InlineData(PortalCapability.RestartTenant)]
    [InlineData(PortalCapability.StopStartTenant)]
    [InlineData(PortalCapability.ManageMembers)]
    [InlineData(PortalCapability.ManageModels)]
    public void A_suspended_stand_is_read_only_even_for_its_owner(PortalCapability capability)
    {
        var decision = PlanCatalog.Decide(
            capability, Stand(status: TenantStatus.Suspended), MembershipRole.Owner, Now);

        Assert.False(decision.Allowed);
        Assert.Contains("suspended", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stand_whose_term_has_ended_is_read_only()
    {
        var decision = PlanCatalog.Decide(
            PortalCapability.RestartTenant,
            Stand(expires: Now.AddSeconds(-1)),
            MembershipRole.Owner,
            Now);

        Assert.False(decision.Allowed);
        Assert.Contains("term has ended", decision.Reason!);
    }

    /// <summary>A term still to run is not a refusal.</summary>
    [Fact]
    public void A_term_still_running_does_not_refuse()
    {
        Assert.True(PlanCatalog.Decide(
            PortalCapability.RestartTenant,
            Stand(expires: Now.AddDays(1)),
            MembershipRole.Owner,
            Now).Allowed);
    }

    [Fact]
    public void A_demo_workspace_is_read_only()
    {
        var decision = PlanCatalog.Decide(
            PortalCapability.RestartTenant, Stand(demo: TenantDemo.Claimed), MembershipRole.Owner, Now);

        Assert.False(decision.Allowed);
        Assert.Contains("demo", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TenantStatus.Provisioning)]
    [InlineData(TenantStatus.Failed)]
    [InlineData(TenantStatus.Deleting)]
    public void A_stand_that_is_not_running_cannot_be_acted_on(TenantStatus status)
    {
        Assert.False(PlanCatalog.Decide(
            PortalCapability.RestartTenant, Stand(status: status), MembershipRole.Owner, Now).Allowed);
    }

    /// <summary>
    /// The everyday reason the portal exists: a bookkeeper who is not the owner
    /// can still restore somebody's access, and can restart a stuck stand.
    /// </summary>
    [Theory]
    [InlineData(PortalCapability.ResetUserPassword)]
    [InlineData(PortalCapability.RestartTenant)]
    public void A_plain_member_can_do_the_everyday_things(PortalCapability capability)
    {
        Assert.True(PlanCatalog.Decide(capability, Stand(), MembershipRole.Member, Now).Allowed);
    }

    [Theory]
    [InlineData(PortalCapability.StopStartTenant)]
    [InlineData(PortalCapability.ManageMembers)]
    [InlineData(PortalCapability.ManageModels)]
    public void Only_an_owner_stops_a_stand_or_changes_who_is_in_it(PortalCapability capability)
    {
        Assert.False(PlanCatalog.Decide(capability, Stand(), MembershipRole.Member, Now).Allowed);
        Assert.True(PlanCatalog.Decide(capability, Stand(), MembershipRole.Owner, Now).Allowed);
    }

    /// <summary>
    /// The plan does NOT gate the buttons, and that is a decision rather than an
    /// oversight — see PlanCatalog's remarks. A Start customer restarts their own
    /// stand exactly as a Business one does, because nothing we published says
    /// otherwise.
    /// </summary>
    [Theory]
    [InlineData(PlanCatalog.Start)]
    [InlineData(PlanCatalog.Business)]
    [InlineData(PlanCatalog.Enterprise)]
    [InlineData(null)]
    public void The_plan_does_not_decide_what_the_buttons_do(string? plan)
    {
        var stand = Stand(plan: plan);

        Assert.True(PlanCatalog.Decide(PortalCapability.RestartTenant, stand, MembershipRole.Owner, Now).Allowed);
        Assert.True(PlanCatalog.Decide(PortalCapability.ResetUserPassword, stand, MembershipRole.Owner, Now).Allowed);
        Assert.True(PlanCatalog.Decide(PortalCapability.StopStartTenant, stand, MembershipRole.Owner, Now).Allowed);
        Assert.True(PlanCatalog.Decide(PortalCapability.ManageModels, stand, MembershipRole.Owner, Now).Allowed);
    }

    /// <summary>
    /// Every refusal carries a sentence. A blank reason reaches the screen as an
    /// unexplained 403, which is the support ticket this portal exists to avoid.
    /// </summary>
    [Fact]
    public void Every_refusal_explains_itself()
    {
        var shapes = new[]
        {
            Stand(status: TenantStatus.Suspended),
            Stand(expires: Now.AddDays(-1)),
            Stand(status: TenantStatus.Failed),
            Stand(demo: TenantDemo.Pooled),
        };

        foreach (var stand in shapes)
        foreach (var capability in Enum.GetValues<PortalCapability>())
        foreach (var role in Enum.GetValues<MembershipRole>())
        {
            var decision = PlanCatalog.Decide(capability, stand, role, Now);
            if (decision.Allowed) continue;

            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
            Assert.EndsWith(".", decision.Reason!.Trim());
        }
    }
}

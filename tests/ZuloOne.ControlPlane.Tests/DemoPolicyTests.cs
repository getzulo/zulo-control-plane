using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Provisioning.Demo;
using ZuloOne.ControlPlane.Registry;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class DemoSlugTests
{
    [Fact]
    public void Mint_produces_a_prefixed_name_of_the_expected_shape()
    {
        for (var i = 0; i < 200; i++)
        {
            var slug = DemoSlug.Mint();

            Assert.StartsWith("demo-", slug, StringComparison.Ordinal);
            Assert.Equal(11, slug.Length);
            Assert.True(DemoSlug.IsDemoSlug(slug));
        }
    }

    /// <summary>
    /// The name is read off a web page and typed into a browser by a stranger.
    /// 0/O and 1/l/i are exactly how that goes wrong.
    /// </summary>
    [Fact]
    public void Mint_never_uses_an_ambiguous_character()
    {
        for (var i = 0; i < 500; i++)
        {
            var body = DemoSlug.Mint()["demo-".Length..];
            Assert.DoesNotContain(body, c => c is '0' or 'o' or '1' or 'l' or 'i');
        }
    }

    /// <summary>A pool name must survive the same gate every slug passes.</summary>
    [Fact]
    public void Mint_never_collides_with_a_reserved_name()
    {
        for (var i = 0; i < 200; i++)
        {
            var slug = DemoSlug.Mint();
            Assert.False(ReservedSlugs.IsReserved(slug), $"'{slug}' is reserved");
            Assert.True(TenantProvisioner.IsValidSlug(slug), $"'{slug}' is not a DNS label");
        }
    }

    [Fact]
    public void Mint_does_not_repeat_itself()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            seen.Add(DemoSlug.Mint());
        }

        // 30^6 ≈ 729M, so 500 draws colliding would mean the generator is broken,
        // not unlucky.
        Assert.Equal(500, seen.Count);
    }

    [Theory]
    [InlineData("demo-k7m2xq", true)]
    [InlineData("DEMO-K7M2XQ", true)]
    [InlineData("demo-acme", true)]
    [InlineData("demo", false)]          // reserved on its own, not in the namespace
    [InlineData("demonstration", false)] // the prefix is "demo-", not "demo"
    [InlineData("acme-demo", false)]
    [InlineData(null, false)]
    public void IsDemoSlug_matches_the_prefix_only(string? slug, bool expected)
    {
        Assert.Equal(expected, DemoSlug.IsDemoSlug(slug));
    }
}

public sealed class DemoLifetimeTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_claimed_demo_past_its_time_is_reapable()
    {
        Assert.True(DemoLifetime.IsReapable(
            TenantDemo.Claimed, TenantOrigin.Provisioned, Now.AddSeconds(-1), Now));
    }

    [Fact]
    public void A_pooled_demo_past_its_time_is_reapable()
    {
        Assert.True(DemoLifetime.IsReapable(
            TenantDemo.Pooled, TenantOrigin.Provisioned, Now, Now));
    }

    [Fact]
    public void One_second_early_is_not_yet()
    {
        Assert.False(DemoLifetime.IsReapable(
            TenantDemo.Claimed, TenantOrigin.Provisioned, Now.AddSeconds(1), Now));
    }

    /// <summary>The golden tenant is the template's origin. Never destroyed.</summary>
    [Fact]
    public void Golden_is_never_reapable_however_expired()
    {
        Assert.False(DemoLifetime.IsReapable(
            TenantDemo.Golden, TenantOrigin.Provisioned, Now.AddYears(-1), Now));
    }

    /// <summary>A real customer. Demo is null and must stay untouchable.</summary>
    [Fact]
    public void A_real_tenant_is_never_reapable()
    {
        Assert.False(DemoLifetime.IsReapable(
            null, TenantOrigin.Provisioned, Now.AddYears(-1), Now));
    }

    /// <summary>
    /// Data the panel never created is not the panel's to destroy — the same
    /// rule the delete path already enforces.
    /// </summary>
    [Fact]
    public void An_adopted_tenant_is_never_reapable()
    {
        Assert.False(DemoLifetime.IsReapable(
            TenantDemo.Claimed, TenantOrigin.Adopted, Now.AddYears(-1), Now));
    }

    [Fact]
    public void No_clock_means_no_reaping()
    {
        Assert.False(DemoLifetime.IsReapable(
            TenantDemo.Claimed, TenantOrigin.Provisioned, null, Now));
    }

    [Fact]
    public void Expiry_is_measured_from_the_moment_given()
    {
        Assert.Equal(Now.AddHours(24), DemoLifetime.ExpiryForClaimed(Now, 24));
        Assert.Equal(Now.AddHours(72), DemoLifetime.ExpiryForPooled(Now, 72));
    }
}

public sealed class DemoQuotaTests
{
    /// <summary>Shipped defaults, so a test reads as the real policy.</summary>
    private static DemoVerdict Decide(
        bool enabled = true,
        int pooledAvailable = 2,
        int liveDemos = 2,
        int queueDepth = 0,
        int perEmailToday = 0,
        int perIpToday = 0,
        bool emailHasLiveDemo = false,
        int maxConcurrent = 6,
        int maxQueue = 20,
        int maxPerEmailPerDay = 1,
        int maxPerIpPerDay = 3) =>
        DemoQuota.Decide(enabled, pooledAvailable, liveDemos, queueDepth, perEmailToday,
            perIpToday, emailHasLiveDemo, maxConcurrent, maxQueue, maxPerEmailPerDay,
            maxPerIpPerDay);

    [Fact]
    public void A_warm_pool_answers_immediately()
    {
        Assert.Equal(DemoVerdict.Ready, Decide());
    }

    [Fact]
    public void Switched_off_overrides_everything()
    {
        Assert.Equal(DemoVerdict.Disabled, Decide(enabled: false, pooledAvailable: 10));
    }

    /// <summary>
    /// Asked twice, still holding one: the honest answer is "here it is again",
    /// not "you have had your allowance".
    /// </summary>
    [Fact]
    public void An_address_already_holding_a_demo_is_told_so()
    {
        Assert.Equal(DemoVerdict.Duplicate, Decide(emailHasLiveDemo: true));
    }

    [Fact]
    public void Duplicate_is_reported_before_the_quota_it_would_also_trip()
    {
        Assert.Equal(
            DemoVerdict.Duplicate,
            Decide(emailHasLiveDemo: true, perEmailToday: 5, perIpToday: 5));
    }

    [Fact]
    public void An_empty_pool_with_room_to_wait_queues()
    {
        Assert.Equal(DemoVerdict.Queue, Decide(pooledAvailable: 0, liveDemos: 5));
    }

    [Fact]
    public void At_the_ceiling_with_a_full_queue_is_full()
    {
        Assert.Equal(
            DemoVerdict.Full,
            Decide(pooledAvailable: 0, liveDemos: 6, queueDepth: 20));
    }

    [Fact]
    public void At_the_ceiling_with_queue_room_still_queues()
    {
        Assert.Equal(
            DemoVerdict.Queue,
            Decide(pooledAvailable: 0, liveDemos: 6, queueDepth: 19));
    }

    [Fact]
    public void One_below_the_ceiling_with_an_empty_pool_queues()
    {
        Assert.Equal(
            DemoVerdict.Queue,
            Decide(pooledAvailable: 0, liveDemos: 5, queueDepth: 20));
    }

    /// <summary>
    /// A pooled workspace is already built and already counted against the
    /// ceiling. Refusing to hand it over would mean holding capacity we have
    /// deliberately paid for.
    /// </summary>
    [Fact]
    public void A_pooled_workspace_is_handed_over_even_at_the_ceiling()
    {
        Assert.Equal(
            DemoVerdict.Ready,
            Decide(pooledAvailable: 1, liveDemos: 6, queueDepth: 20));
    }

    [Fact]
    public void The_email_quota_bites_at_the_limit_not_after()
    {
        Assert.Equal(DemoVerdict.TooManyForEmail, Decide(perEmailToday: 1));
        Assert.Equal(DemoVerdict.Ready, Decide(perEmailToday: 0));
    }

    [Fact]
    public void The_address_quota_bites_at_the_limit_not_after()
    {
        Assert.Equal(DemoVerdict.TooManyForIp, Decide(perIpToday: 3));
        Assert.Equal(DemoVerdict.Ready, Decide(perIpToday: 2));
    }

    [Fact]
    public void The_email_quota_is_reported_before_the_address_quota()
    {
        // Both tripped: the caller can act on their own address, not on whoever
        // else shares their network.
        Assert.Equal(
            DemoVerdict.TooManyForEmail,
            Decide(perEmailToday: 9, perIpToday: 9));
    }

    [Fact]
    public void A_zero_ceiling_closes_the_door_without_disabling_the_feature()
    {
        Assert.Equal(
            DemoVerdict.Full,
            Decide(pooledAvailable: 0, liveDemos: 0, maxConcurrent: 0, queueDepth: 0, maxQueue: 0));
    }
}

public sealed class DemoPoolServiceTests
{
    [Theory]
    [InlineData(0, 0, 0, 2, 6, 2)]
    [InlineData(1, 0, 1, 2, 6, 1)]
    [InlineData(1, 1, 2, 2, 6, 0)]
    [InlineData(0, 0, 6, 2, 6, 0)]
    [InlineData(0, 1, 5, 2, 6, 0)]
    [InlineData(5, 0, 5, 2, 6, 0)]
    public void Build_count_respects_pool_target_and_host_ceiling(
        int available,
        int scheduled,
        int live,
        int poolTarget,
        int maxConcurrent,
        int expected)
    {
        Assert.Equal(expected, DemoPoolService.CalculateBuildCount(
            available, scheduled, live, poolTarget, maxConcurrent));
    }
}

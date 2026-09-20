using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning.Demo;
using ZuloOne.ControlPlane.Registry;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

/// <summary>
/// The pool claim against a REAL Postgres.
/// </summary>
/// <remarks>
/// <para>
/// These cannot be faked. The whole behaviour under test is what the database
/// does when two statements touch one row at the same moment, and the EF
/// in-memory provider has no row locks — it would report success for both
/// callers and prove the opposite of the truth.
/// </para>
/// <para>
/// Gated on <c>TEST_PG_CONNECTION</c> and skipped when it is absent, so a
/// developer without a database still gets a green suite. Run it with:
/// </para>
/// <code>
/// docker run -d --name cp-test -e POSTGRES_PASSWORD=probe -e POSTGRES_DB=cptest \
///     -p 55432:5432 postgres:17-alpine
/// TEST_PG_CONNECTION="Host=localhost;Port=55432;Database=cptest;Username=postgres;Password=probe" \
///     dotnet test
/// </code>
/// <para>
/// A skipped test protects nothing, and that is a real cost — but a claim tested
/// against a fake is worse, because it reads as covered.
/// </para>
/// </remarks>
public sealed class DemoPoolConcurrencyTests : IAsyncLifetime
{
    private static string? Connection => Environment.GetEnvironmentVariable("TEST_PG_CONNECTION");

    private static bool Available => !string.IsNullOrWhiteSpace(Connection);

    private const string SkipReason =
        "Set TEST_PG_CONNECTION to a scratch Postgres to run the pool-claim tests.";

    private readonly List<ControlPlaneDbContext> _contexts = [];

    public async Task InitializeAsync()
    {
        if (!Available) return;

        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        foreach (var context in _contexts) context.Dispose();
        return Task.CompletedTask;
    }

    private ControlPlaneDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(Connection)
            .Options;
        var db = new ControlPlaneDbContext(options);
        _contexts.Add(db);
        return db;
    }

    /// <summary>The shipped default, so a test reads as the real policy.</summary>
    private const int LifetimeHours = 24;

    private static Tenant PooledTenant(string slug, DateTime created) => new()
    {
        Slug = slug,
        Status = TenantStatus.Active,
        Health = TenantHealth.Ok,
        Origin = TenantOrigin.Provisioned,
        Demo = TenantDemo.Pooled,
        ImageTag = "zuloone/core:dev",
        CreatedAt = created,
        UpdatedAt = created,
        ExpiresAt = created.AddHours(72),
    };

    private async Task SeedAsync(int count)
    {
        await using var db = NewContext();
        var start = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            db.Tenants.Add(PooledTenant($"demo-seed{i:D2}", start.AddMinutes(i)));
        }

        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task Five_racing_callers_against_two_workspaces_get_exactly_two()
    {
        Skip.IfNot(Available, SkipReason);
        await SeedAsync(2);

        var now = DateTime.UtcNow;
        var requests = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        // Each caller gets its OWN context, as each HTTP request would. Sharing one
        // would serialise them through EF's change tracker and prove nothing.
        var results = await Task.WhenAll(requests.Select(async id =>
        {
            await using var db = NewContext();
            var pool = new DemoPool(db);
            return await pool.TryClaimAsync(id, now, LifetimeHours, CancellationToken.None);
        }));

        var won = results.Where(t => t is not null).ToArray();
        Assert.Equal(2, won.Length);

        // Two winners, two DIFFERENT workspaces. One workspace handed to two
        // visitors is the exact failure this design exists to prevent.
        Assert.Equal(2, won.Select(t => t!.Slug).Distinct().Count());

        await using var check = NewContext();
        Assert.Equal(0, await check.Tenants.CountAsync(t => t.Demo == TenantDemo.Pooled));
        Assert.Equal(2, await check.Tenants.CountAsync(t => t.Demo == TenantDemo.Claimed));
    }

    [SkippableFact]
    public async Task Every_caller_wins_when_there_is_enough_to_go_round()
    {
        Skip.IfNot(Available, SkipReason);
        await SeedAsync(5);

        var now = DateTime.UtcNow;
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var db = NewContext();
            var pool = new DemoPool(db);
            return await pool.TryClaimAsync(Guid.NewGuid(), now, LifetimeHours, CancellationToken.None);
        }));

        Assert.All(results, t => Assert.NotNull(t));
        Assert.Equal(5, results.Select(t => t!.Slug).Distinct().Count());
    }

    [SkippableFact]
    public async Task An_empty_pool_answers_null_rather_than_waiting()
    {
        Skip.IfNot(Available, SkipReason);

        await using var db = NewContext();
        var pool = new DemoPool(db);

        Assert.Null(await pool.TryClaimAsync(Guid.NewGuid(), DateTime.UtcNow, LifetimeHours, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_claim_stamps_the_request_and_starts_the_clock()
    {
        Skip.IfNot(Available, SkipReason);
        await SeedAsync(1);

        var requestId = Guid.NewGuid();
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        await using var db = NewContext();
        var claimed = await new DemoPool(db)
            .TryClaimAsync(requestId, now, LifetimeHours, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(TenantDemo.Claimed, claimed!.Demo);
        Assert.Equal(requestId, claimed.DemoRequestId);
        Assert.Equal(now, claimed.ClaimedAt);
        // The default lifetime is 24h, and the clock starts at the CLAIM — time
        // spent sitting in the pool was never the visitor's.
        Assert.Equal(now.AddHours(24), claimed.ExpiresAt);
    }

    /// <summary>The oldest workspace is the one closest to being retired anyway.</summary>
    [SkippableFact]
    public async Task The_oldest_workspace_is_spent_first()
    {
        Skip.IfNot(Available, SkipReason);
        await SeedAsync(3);

        await using var db = NewContext();
        var claimed = await new DemoPool(db)
            .TryClaimAsync(Guid.NewGuid(), DateTime.UtcNow, LifetimeHours, CancellationToken.None);

        Assert.Equal("demo-seed00", claimed!.Slug);
    }

    /// <summary>
    /// A workspace whose container is not answering must not be handed over —
    /// the visitor would meet a dead page and conclude the product is dead.
    /// </summary>
    [SkippableFact]
    public async Task An_unhealthy_workspace_is_not_handed_over()
    {
        Skip.IfNot(Available, SkipReason);

        await using var seed = NewContext();
        var t = PooledTenant("demo-sick01", new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc));
        t.Health = TenantHealth.Down;
        seed.Tenants.Add(t);
        await seed.SaveChangesAsync();

        await using var db = NewContext();
        Assert.Null(await new DemoPool(db)
            .TryClaimAsync(Guid.NewGuid(), DateTime.UtcNow, LifetimeHours, CancellationToken.None));
    }
}

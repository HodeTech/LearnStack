using FluentAssertions;
using LearnStack.Infrastructure.Caching;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Caching;

/// <summary>
/// The default <see cref="ICacheService"/>, per
/// <see href="../../../../../docs/decisions/0014-adopt-dapr.md">ADR-0014</see>
/// and its Amendment 2.
/// </summary>
/// <remarks>
/// Every expiry case moves a <see cref="FixedClock"/> rather than sleeping, so
/// the TTL behaviour is asserted rather than approximated — the same reason
/// <c>InMemoryIdempotencyStore</c> takes a clock.
/// </remarks>
public sealed class InMemoryCacheServiceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    private static readonly Guid OtherTenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000b");

    private static string Key(Guid tenant = default) =>
        CacheKey.For(tenant == default ? Tenant : tenant, "tenancy", "settings");

    [Fact]
    public async Task A_Miss_Is_Default_Not_An_Error()
    {
        var (cache, _) = New();

        (await cache.GetAsync<string>(Key())).Should().BeNull();
    }

    [Fact]
    public async Task What_Was_Set_Is_What_Is_Read()
    {
        var (cache, _) = New();

        await cache.SetAsync(Key(), "value");

        (await cache.GetAsync<string>(Key())).Should().Be("value");
    }

    [Fact]
    public async Task A_Removed_Key_Is_A_Miss()
    {
        var (cache, _) = New();
        await cache.SetAsync(Key(), "value");

        await cache.RemoveAsync(Key());

        (await cache.GetAsync<string>(Key())).Should().BeNull();
    }

    [Fact]
    public async Task Removing_A_Key_That_Is_Not_There_Is_Not_An_Error()
    {
        var (cache, _) = New();

        var act = () => cache.RemoveAsync(Key());

        await act.Should().NotThrowAsync();
    }

    // ---- the key is the isolation boundary ---------------------------------

    [Fact]
    public async Task One_Tenants_Value_Is_Not_Another_Tenants()
    {
        // There is no query filter in front of a dictionary. If this ever fails,
        // the cache is a cross-tenant read.
        var (cache, _) = New();

        await cache.SetAsync(Key(Tenant), "mine");

        (await cache.GetAsync<string>(Key(OtherTenant))).Should().BeNull();
    }

    [Theory]
    [InlineData("tenancy:settings")]
    [InlineData("settings")]
    public async Task Every_Entry_Point_Refuses_A_Key_Without_A_Tenant(string key)
    {
        // All four, not just one: a guard on Get that Set does not share is a
        // guard a writer walks straight past.
        var (cache, _) = New();

        await ((Func<Task>)(() => cache.GetAsync<string>(key)))
            .Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => cache.SetAsync(key, "v")))
            .Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => cache.RemoveAsync(key)))
            .Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => cache.GetOrSetAsync(key, _ => Task.FromResult("v"))))
            .Should().ThrowAsync<ArgumentException>();
    }

    // ---- expiry ------------------------------------------------------------

    [Fact]
    public async Task An_Entry_Expires_At_Its_Ttl()
    {
        var (cache, clock) = New();
        await cache.SetAsync(Key(), "value");

        clock.Advance(InMemoryCacheService.DefaultTtl);

        (await cache.GetAsync<string>(Key())).Should().BeNull();
    }

    [Fact]
    public async Task An_Entry_Just_Inside_Its_Ttl_Is_Still_There()
    {
        var (cache, clock) = New();
        await cache.SetAsync(Key(), "value");

        clock.Advance(InMemoryCacheService.DefaultTtl - TimeSpan.FromSeconds(1));

        (await cache.GetAsync<string>(Key())).Should().Be("value");
    }

    [Fact]
    public async Task A_Caller_Supplied_Ttl_Wins_Over_The_Default()
    {
        var (cache, clock) = New();

        await cache.SetAsync(Key(), "value", new CacheOptions(L1Ttl: TimeSpan.FromHours(1)));
        clock.Advance(InMemoryCacheService.DefaultTtl * 2);

        (await cache.GetAsync<string>(Key())).Should().Be("value",
            "the default is what a caller gets when it names none, not a ceiling");
    }

    [Fact]
    public async Task L2Ttl_Is_Carried_And_Ignored()
    {
        // There is no second layer here. The value exists so a caller written
        // today does not change when the Valkey adapter gives it a meaning.
        var (cache, clock) = New();

        await cache.SetAsync(Key(), "value", new CacheOptions(L2Ttl: TimeSpan.FromHours(1)));
        clock.Advance(InMemoryCacheService.DefaultTtl);

        (await cache.GetAsync<string>(Key())).Should().BeNull();
    }

    // ---- GetOrSet ----------------------------------------------------------

    [Fact]
    public async Task GetOrSet_Produces_On_A_Miss_And_Stores_What_It_Produced()
    {
        var (cache, _) = New();

        var produced = await cache.GetOrSetAsync(Key(), _ => Task.FromResult("made"));

        produced.Should().Be("made");
        (await cache.GetAsync<string>(Key())).Should().Be("made");
    }

    [Fact]
    public async Task GetOrSet_Does_Not_Produce_On_A_Hit()
    {
        var (cache, _) = New();
        await cache.SetAsync(Key(), "cached");

        var calls = 0;
        var value = await cache.GetOrSetAsync(Key(), _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("made");
        });

        value.Should().Be("cached");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_Misses_Run_The_Factory_Once()
    {
        // The factory is the expensive side — a database round trip, a Hub call.
        // A cache that lets N simultaneous misses each run it turns a cold key
        // into a stampede against the dependency it exists to spare.
        var (cache, _) = New();
        var calls = 0;
        using var gate = new SemaphoreSlim(0);

        async Task<string> Factory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await gate.WaitAsync(TestTimeout, cancellationToken);
            return "made";
        }

        var callers = Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrSetAsync(Key(), Factory))
            .ToArray();

        gate.Release(8);
        var results = await Task.WhenAll(callers);

        calls.Should().Be(1, "one flight per key, however many callers miss at once");
        results.Should().AllBe("made");
    }

    [Fact]
    public async Task A_Failed_Flight_Does_Not_Poison_The_Key()
    {
        // The next caller must get a fresh attempt, not the cached exception.
        var (cache, _) = New();

        await ((Func<Task>)(() => cache.GetOrSetAsync<string>(
                Key(), _ => throw new InvalidOperationException("boom"))))
            .Should().ThrowAsync<InvalidOperationException>();

        var recovered = await cache.GetOrSetAsync(Key(), _ => Task.FromResult("second"));

        recovered.Should().Be("second");
    }

    // ---- bound -------------------------------------------------------------

    [Fact]
    public async Task The_Map_Is_Bounded()
    {
        // A cache with no bound is an out-of-memory condition waiting for a
        // caller with an unbounded key space. Evicting a live entry is allowed
        // here — a miss costs a round trip — which is what makes this bound
        // simpler than the idempotency store's.
        var (cache, clock) = New();

        // The TTL must outlast the whole run, or the oldest entries expire and
        // the assertion below passes for the wrong reason — measured: with a
        // one-hour TTL and a one-second advance per entry, the run covers about
        // three hours and `k000000` is gone because it EXPIRED. Deleting the
        // bound then changed nothing.
        var ttl = new CacheOptions(L1Ttl: TimeSpan.FromDays(30));

        for (var i = 0; i <= InMemoryCacheService.MaxEntries + 500; i++)
        {
            await cache.SetAsync(CacheKey.For(Tenant, "tenancy", $"k{i:D6}"), i, ttl);
            clock.Advance(InMemoryCacheService.SweepInterval);
        }

        (await cache.GetAsync<int?>(CacheKey.For(Tenant, "tenancy", "k000000")))
            .Should().BeNull("the oldest entries are the ones the bound drops");

        var newest = InMemoryCacheService.MaxEntries + 500;
        (await cache.GetAsync<int?>(CacheKey.For(Tenant, "tenancy", $"k{newest:D6}")))
            .Should().Be(newest);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static (InMemoryCacheService Cache, FixedClock Clock) New()
    {
        var clock = new FixedClock(Origin);
        return (new InMemoryCacheService(clock), clock);
    }
}

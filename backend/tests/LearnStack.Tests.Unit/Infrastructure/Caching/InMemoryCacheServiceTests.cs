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
    public async Task The_Map_Is_Bounded_Even_When_The_Clock_Never_Moves()
    {
        // The clock is FROZEN, which is the case the first version got wrong: the
        // bound was enforced only inside a sweep, the sweep is throttled by clock
        // time, and a burst does not advance the clock. Measured then: 60,000
        // entries against a ceiling of 10,000. The test that "covered" the bound
        // advanced the clock one second per write — the one schedule under which
        // the old code held.
        var (cache, _) = New();
        var ttl = new CacheOptions(L1Ttl: TimeSpan.FromDays(30));

        for (var i = 0; i <= InMemoryCacheService.MaxEntries + 500; i++)
        {
            await cache.SetAsync(CacheKey.For(Tenant, "tenancy", $"k{i:D6}"), i, ttl);
        }

        cache.Count.Should().BeLessThanOrEqualTo(InMemoryCacheService.MaxEntries,
            "the ceiling is a count, so that is what the test asserts");

        (await cache.GetAsync<int?>(CacheKey.For(Tenant, "tenancy", "k000000")))
            .Should().BeNull("the oldest entries are the ones the bound drops");

        var newest = InMemoryCacheService.MaxEntries + 500;
        (await cache.GetAsync<int?>(CacheKey.For(Tenant, "tenancy", $"k{newest:D6}")))
            .Should().Be(newest, "the newest write is never the one evicted");
    }

    [Fact]
    public async Task Replacing_A_Key_Does_Not_Grow_The_Map()
    {
        // Only a write that ADDS a key can cross the ceiling, which is why the
        // bound is checked on TryAdd rather than on every write.
        var (cache, _) = New();
        var ttl = new CacheOptions(L1Ttl: TimeSpan.FromDays(30));

        for (var i = 0; i < InMemoryCacheService.MaxEntries * 2; i++)
        {
            await cache.SetAsync(Key(), i, ttl);
        }

        (await cache.GetAsync<int?>(Key())).Should().Be((InMemoryCacheService.MaxEntries * 2) - 1);
    }

    // ---- cancellation is not contagious -------------------------------------

    [Fact]
    public async Task One_Callers_Cancellation_Does_Not_Fail_The_Others()
    {
        // The first version handed the shared flight the winning caller's token.
        // Measured: one client pressing refresh cancelled the factory and every
        // other request waiting on that key died with it — as a 499, which this
        // host treats as "the client hung up", so it writes no body, captures no
        // error and records no span. A request that did nothing wrong failed
        // invisibly.
        var (cache, _) = New();
        using var release = new SemaphoreSlim(0);
        using var leaving = new CancellationTokenSource();

        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Factory(CancellationToken cancellationToken)
        {
            factoryEntered.TrySetResult();
            await release.WaitAsync(TestTimeout, cancellationToken);
            return "made";
        }

        var leaves = cache.GetOrSetAsync(Key(), Factory, null, leaving.Token);
        await factoryEntered.Task.WaitAsync(TestTimeout);
        var stays = cache.GetOrSetAsync(Key(), Factory, null, CancellationToken.None);

        await leaving.CancelAsync();
        await ((Func<Task>)(() => leaves)).Should().ThrowAsync<OperationCanceledException>();

        release.Release(2);

        (await stays).Should().Be("made",
            "the caller that stayed connected asked for nothing that failed");
    }

    [Fact]
    public async Task A_Joiner_Can_Abandon_A_Slow_Flight()
    {
        // The mirror image: before, a joiner awaited the shared task directly and
        // could not leave until the winner's factory finished.
        var (cache, _) = New();
        using var release = new SemaphoreSlim(0);
        using var impatient = new CancellationTokenSource();
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Factory(CancellationToken cancellationToken)
        {
            factoryEntered.TrySetResult();
            await release.WaitAsync(TestTimeout, cancellationToken);
            return "made";
        }

        var winner = cache.GetOrSetAsync(Key(), Factory, null, CancellationToken.None);
        await factoryEntered.Task.WaitAsync(TestTimeout);
        var joiner = cache.GetOrSetAsync(Key(), Factory, null, impatient.Token);

        await impatient.CancelAsync();

        await ((Func<Task>)(() => joiner)).Should().ThrowAsync<OperationCanceledException>();

        release.Release();
        (await winner).Should().Be("made", "the flight itself was never cancelled");
    }

    // ---- an invalidation during a flight is not undone ----------------------

    [Fact]
    public async Task A_Remove_During_A_Flight_Is_Not_Resurrected_By_It()
    {
        // Eager invalidation must not be silently lost for a full TTL because it
        // happened to land while a factory was running.
        var (cache, _) = New();
        using var release = new SemaphoreSlim(0);
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var flight = cache.GetOrSetAsync(Key(), async _ =>
        {
            factoryEntered.TrySetResult();
            await release.WaitAsync(TestTimeout, CancellationToken.None);
            return "stale";
        });

        await factoryEntered.Task.WaitAsync(TestTimeout);
        await cache.RemoveAsync(Key());
        release.Release();

        (await flight).Should().Be("stale", "the caller still gets what it asked for");
        (await cache.GetAsync<string>(Key())).Should().BeNull(
            "but the value it produced is not written over the invalidation");
    }

    [Fact]
    public async Task A_Set_During_A_Flight_Wins_Over_It()
    {
        var (cache, _) = New();
        using var release = new SemaphoreSlim(0);
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var flight = cache.GetOrSetAsync(Key(), async _ =>
        {
            factoryEntered.TrySetResult();
            await release.WaitAsync(TestTimeout, CancellationToken.None);
            return "from-factory";
        });

        await factoryEntered.Task.WaitAsync(TestTimeout);
        await cache.SetAsync(Key(), "explicit");
        release.Release();

        await flight;

        (await cache.GetAsync<string>(Key())).Should().Be("explicit",
            "the newer write is the one that stands");
    }

    [Fact]
    public async Task Expired_Entries_Are_Reclaimed_Without_Waiting_For_The_Ceiling()
    {
        // Expired entries are never READ — IsFresh guards both read paths — so
        // failing to reclaim them is invisible in every value the cache returns.
        // It is still a leak: without this, a workload whose keys all expire holds
        // every one of them until the map crosses MaxEntries, which for a small
        // key space is never.
        var (cache, clock) = New();
        for (var i = 0; i < 200; i++)
        {
            await cache.SetAsync(CacheKey.For(Tenant, "tenancy", $"k{i}"), i);
        }

        cache.Count.Should().Be(200);

        clock.Advance(InMemoryCacheService.DefaultTtl + InMemoryCacheService.SweepInterval);
        await cache.GetAsync<int?>(CacheKey.For(Tenant, "tenancy", "trigger"));

        cache.Count.Should().Be(0, "a sweep reclaims what expired, bound or no bound");
    }

    // ---- the TTL boundary ---------------------------------------------------

    [Fact]
    public async Task An_Entry_Is_Gone_At_Exactly_Its_Expiry_Instant()
    {
        // `now < ExpiresAt`, not `<=`. The old "just inside its TTL" case moved a
        // whole second short of the boundary and would have passed either way.
        var (cache, clock) = New();
        await cache.SetAsync(Key(), "value");

        clock.Advance(InMemoryCacheService.DefaultTtl - TimeSpan.FromTicks(1));
        (await cache.GetAsync<string>(Key())).Should().Be("value", "one tick before expiry");

        clock.Advance(TimeSpan.FromTicks(1));
        (await cache.GetAsync<string>(Key())).Should().BeNull("at the expiry instant");
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static (InMemoryCacheService Cache, FixedClock Clock) New()
    {
        var clock = new FixedClock(Origin);
        return (new InMemoryCacheService(clock), clock);
    }
}

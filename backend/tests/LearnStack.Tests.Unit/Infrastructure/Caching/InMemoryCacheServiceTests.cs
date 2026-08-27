using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Infrastructure.Caching;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Caching;

/// <summary>
/// The default <see cref="ICacheService"/>, per
/// <see href="../../../../../docs/decisions/0038-cross-cutting-port-and-event-contracts.md">ADR-0038</see>.
/// </summary>
/// <remarks>
/// Every expiry case moves a <see cref="FixedClock"/> rather than sleeping, so
/// the TTL behaviour is asserted rather than approximated — the same reason
/// <c>InMemoryIdempotencyStore</c> takes a clock.
/// </remarks>
public sealed class InMemoryCacheServiceTests
{
    private static readonly ServiceProvider MeterServices = new ServiceCollection()
        .AddMetrics()
        .BuildServiceProvider();

    private static IMeterFactory MeterFactory =>
        MeterServices.GetRequiredService<IMeterFactory>();

    private static readonly DateTimeOffset Origin = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    private static readonly Guid OtherTenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000b");

    private static string Key(Guid tenant = default) =>
        CacheKey.ForTenant(tenant == default ? Tenant : tenant, "tenancy", "settings");

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

    [Fact]
    public async Task Invalid_Ttls_Are_Rejected_Before_The_Factory_Runs()
    {
        var (cache, _) = New();
        var calls = 0;
        var invalid = new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.MaxValue };

        foreach (var ttl in invalid)
        {
            var act = () => cache.GetOrSetAsync(
                Key(),
                _ =>
                {
                    calls++;
                    return Task.FromResult("value");
                },
                new CacheOptions(L1Ttl: ttl));

            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        var invalidL2 = () => cache.GetOrSetAsync(
            Key(),
            _ =>
            {
                calls++;
                return Task.FromResult("value");
            },
            new CacheOptions(L2Ttl: TimeSpan.Zero));

        await invalidL2.Should().ThrowAsync<ArgumentOutOfRangeException>();
        calls.Should().Be(0);
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

        // Dispatched through Task.Run and held at a barrier, NOT built with
        // Select(...).ToArray(). Measured: LINQ evaluates sequentially on one
        // thread, so each caller ran to its first suspension point before the
        // next was even invoked — caller 1 had already registered its flight
        // before caller 2 existed. Nothing ever raced, and the mutation this
        // test exists to catch (LazyThreadSafetyMode.None) survived it, while
        // failing 5 out of 5 runs once the callers actually ran concurrently.
        using var start = new ManualResetEventSlim(false);
        var callers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait(TestTimeout);
                return await cache.GetOrSetAsync(Key(), Factory);
            }))
            .ToArray();

        start.Set();
        gate.Release(16);
        var results = await Task.WhenAll(callers);

        calls.Should().Be(1, "one flight per key, however many callers miss at once");
        results.Should().AllBe("made");
    }

    [Fact]
    public async Task The_Flight_Owners_Ttl_Is_The_One_Stored()
    {
        var (cache, clock) = New();
        using var release = new SemaphoreSlim(0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var owner = cache.GetOrSetAsync(
            Key(),
            async cancellationToken =>
            {
                entered.TrySetResult();
                await release.WaitAsync(TestTimeout, cancellationToken);
                return "made";
            },
            new CacheOptions(L1Ttl: TimeSpan.FromSeconds(2)));

        await entered.Task.WaitAsync(TestTimeout);
        var joiner = cache.GetOrSetAsync(
            Key(),
            _ => Task.FromResult("unused"),
            new CacheOptions(L1Ttl: TimeSpan.FromHours(1)));

        release.Release();
        await Task.WhenAll(owner, joiner);
        clock.Advance(TimeSpan.FromSeconds(2));

        (await cache.GetAsync<string>(Key())).Should().BeNull(
            "the first caller owns the one shared factory and its cache policy");
    }

    [Fact]
    public async Task Cache_Metrics_Use_Stable_Low_Cardinality_Names()
    {
        var measurements = new ConcurrentBag<(string Instrument, string CacheName)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == InMemoryCacheService.MeterName)
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var cacheName = tags.ToArray()
                .Single(tag => tag.Key == "cache.name")
                .Value
                ?.ToString();
            measurements.Add((instrument.Name, cacheName!));
        });
        listener.Start();

        var (cache, _) = New();
        await cache.GetAsync<string>(Key());
        await cache.SetAsync(Key(), "value");
        await cache.GetAsync<string>(Key());
        await cache.RemoveAsync(Key());
        await cache.GetAsync<string>(
            CacheKey.ForTenant(Tenant, "tenancy", OtherTenant.ToString()));

        using var release = new SemaphoreSlim(0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = cache.GetOrSetAsync(Key(), async cancellationToken =>
        {
            entered.TrySetResult();
            await release.WaitAsync(TestTimeout, cancellationToken);
            return "coalesced";
        });
        await entered.Task.WaitAsync(TestTimeout);
        var joiner = cache.GetOrSetAsync(Key(), _ => Task.FromResult("unused"));
        release.Release();
        await Task.WhenAll(owner, joiner);

        measurements.Select(measurement => measurement.Instrument).Should().Contain(
            [
                InMemoryCacheService.MissCounterName,
                InMemoryCacheService.StoreCounterName,
                InMemoryCacheService.HitCounterName,
                InMemoryCacheService.CoalescedCounterName,
            ]);
        measurements.Should().OnlyContain(measurement =>
            measurement.CacheName == "tenancy:settings"
            || measurement.CacheName == "other");
        measurements.Should().OnlyContain(measurement =>
            !measurement.CacheName.Contains(Tenant.ToString(), StringComparison.Ordinal)
            && !measurement.CacheName.Contains(OtherTenant.ToString(), StringComparison.Ordinal));
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
            await cache.SetAsync(CacheKey.ForTenant(Tenant, "tenancy", $"k{i:D6}"), i, ttl);
        }

        // Between the low-water mark and the ceiling. A trim evicts down to
        // TrimTarget rather than back to MaxEntries, deliberately: without that
        // gap the steady state of an unbounded key space is a trim on every
        // single write, each one copying and sorting the whole map to drop one
        // entry. Both ends are asserted, because "bounded" that never evicts
        // and "bounded" that empties itself are both wrong.
        cache.Count.Should().BeInRange(
            InMemoryCacheService.TrimTarget, InMemoryCacheService.MaxEntries);

        (await cache.GetAsync<int?>(CacheKey.ForTenant(Tenant, "tenancy", "k000000")))
            .Should().BeNull("the oldest entries are the ones the bound drops");

        var newest = InMemoryCacheService.MaxEntries + 500;
        (await cache.GetAsync<int?>(CacheKey.ForTenant(Tenant, "tenancy", $"k{newest:D6}")))
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

        cache.Count.Should().Be(1,
            "which is the invariant this test names — asserting only that the "
            + "last write wins would pass however Store branched, since one key "
            + "cannot occupy two slots in a dictionary");
        (await cache.GetAsync<int?>(Key())).Should().Be((InMemoryCacheService.MaxEntries * 2) - 1);
    }

    [Fact]
    public async Task Writing_At_The_Ceiling_From_Several_Threads_Throws_Nothing()
    {
        // The eviction pass used to run LINQ over the LIVE dictionary, which
        // buffers it through ICollection.CopyTo after reading Count — two steps
        // that are not atomic. Grow in between and CopyTo throws
        // ArgumentException; shrink and the buffer's tail keeps a default
        // KeyValuePair whose Value is null, which the sort key dereferences.
        // Both escaped into SetAsync and GetOrSetAsync. Measured on that
        // version: two concurrent writers were enough — 4.1% of ordinary writes
        // threw, four writers 15.5% — and the whole existing suite stayed green,
        // because every other test drives the eviction from one thread with
        // `await` in a `for` loop.
        //
        // A component whose contract is that it may no-op at any time must never
        // fail the caller's request. This asserts exactly that, and nothing about
        // which entries survive.
        var (cache, _) = New();
        var ttl = new CacheOptions(L1Ttl: TimeSpan.FromDays(30));

        for (var i = 0; i < InMemoryCacheService.MaxEntries; i++)
        {
            await cache.SetAsync(CacheKey.ForTenant(Tenant, "tenancy", $"warm{i:D6}"), i, ttl);
        }

        var failures = new ConcurrentBag<Exception>();
        using var start = new ManualResetEventSlim(false);

        var writers = Enumerable.Range(0, 4).Select(t => Task.Run(async () =>
        {
            start.Wait(TestTimeout);
            for (var i = 0; i < 1_500; i++)
            {
                try
                {
                    await cache.SetAsync(
                        CacheKey.ForTenant(Tenant, "tenancy", $"t{t}k{i:D6}"), i, ttl);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(writers);

        failures.Should().BeEmpty(
            "eviction is the cache's own business and never the caller's error");
        cache.Count.Should().BeLessThanOrEqualTo(InMemoryCacheService.MaxEntries);
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

    [Fact]
    public async Task A_Joiner_That_Leaves_Does_Not_Restart_The_Factory_For_The_Next_Arrival()
    {
        // The first version unregistered the shared flight in a `finally`, which
        // runs when a caller stops WAITING — including when it stops by
        // cancelling. A joiner that walked away therefore removed the
        // registration while the factory was still running, and the next
        // arrival started a second concurrent run: the exact stampede this
        // method exists to prevent, reintroduced by its own cleanup, in the same
        // change whose comment promised a joiner could leave without affecting
        // the others.
        var (cache, _) = New();
        using var release = new SemaphoreSlim(0);
        using var leaving = new CancellationTokenSource();
        var runs = 0;
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            factoryEntered.TrySetResult();
            await release.WaitAsync(TestTimeout, CancellationToken.None);
            return "made";
        }

        var stays = cache.GetOrSetAsync(Key(), Factory, null, CancellationToken.None);
        await factoryEntered.Task.WaitAsync(TestTimeout);
        var leaves = cache.GetOrSetAsync(Key(), Factory, null, leaving.Token);

        await leaving.CancelAsync();
        await ((Func<Task>)(() => leaves)).Should().ThrowAsync<OperationCanceledException>();

        // The next arrival must JOIN the still-running flight, not start one.
        var arrives = cache.GetOrSetAsync(Key(), Factory, null, CancellationToken.None);

        release.Release(3);
        (await stays).Should().Be("made");
        (await arrives).Should().Be("made");

        runs.Should().Be(1, "one factory run per key, however many callers come and go");
    }

    [Fact]
    public async Task An_Abandoned_Factory_Must_Terminate_Before_Its_Replacement_Starts()
    {
        var (cache, _) = New();
        using var release = new SemaphoreSlim(0);
        using var abandoning = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var running = 0;
        var maximumRunning = 0;

        var abandoned = cache.GetOrSetAsync(Key(), async _ =>
        {
            Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref running);
            RecordMaximum(ref maximumRunning, current);
            entered.TrySetResult();
            await release.WaitAsync(TestTimeout, CancellationToken.None);
            Interlocked.Decrement(ref running);
            return "abandoned";
        }, null, abandoning.Token);

        await entered.Task.WaitAsync(TestTimeout);
        await abandoning.CancelAsync();
        await ((Func<Task>)(() => abandoned)).Should().ThrowAsync<OperationCanceledException>();

        cache.InFlightCount.Should().Be(1,
            "abandonment cancels the factory but cannot pretend ignored cancellation has ended it");

        var next = cache.GetOrSetAsync(Key(), _ =>
        {
            Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref running);
            RecordMaximum(ref maximumRunning, current);
            Interlocked.Decrement(ref running);
            return Task.FromResult("fresh");
        });

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        calls.Should().Be(1, "the replacement waits for actual terminality");

        release.Release();
        (await next.WaitAsync(TestTimeout)).Should().Be("fresh");
        calls.Should().Be(2);
        maximumRunning.Should().Be(1, "same-key factories never overlap");
    }

    [Fact]
    public async Task A_Caller_Arriving_After_A_Remove_Does_Not_Join_The_Doomed_Flight()
    {
        // Supersede only stops a flight from STORING. A caller whose
        // GetOrSetAsync begins strictly after RemoveAsync returned would
        // otherwise miss _entries — the Remove emptied it — join the doomed
        // flight, and be handed the value the invalidation existed to kill,
        // its own factory never invoked. Callers already in flight when the
        // write landed are a different case: that is an ordinary race, and they
        // keep their result.
        var (cache, _) = New();
        using var release = new SemaphoreSlim(0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = cache.GetOrSetAsync(Key(), async _ =>
        {
            entered.TrySetResult();
            await release.WaitAsync(TestTimeout, CancellationToken.None);
            return "before-the-remove";
        });

        await entered.Task.WaitAsync(TestTimeout);
        await cache.RemoveAsync(Key());

        var afterwards = cache.GetOrSetAsync(
            Key(), _ => Task.FromResult("after-the-remove"));

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        afterwards.IsCompleted.Should().BeFalse(
            "the replacement must not overlap the superseded factory");

        release.Release();
        (await inFlight).Should().Be("before-the-remove",
            "the caller already in flight still gets what it asked for");
        (await afterwards).Should().Be("after-the-remove",
            "it started after the invalidation, so it reads the source of truth");
    }

    [Fact]
    public async Task A_Faulted_Flight_Nobody_Awaits_Leaves_No_Unobserved_Exception()
    {
        // The correlated failure: a factory faults when a dependency is down,
        // and a dependency being down is exactly when clients time out and
        // disconnect. With every caller gone nobody awaits the task, so its
        // exception goes unobserved and TaskScheduler.UnobservedTaskException
        // fires — with no request, no span and no correlation id attached, and
        // a host configured with ThrowUnobservedTaskExceptions terminates on it.
        // Measured on the shape this reproduces: 20 of 20 abandoned faulted
        // flights raised the event without the observation, 0 of 20 with it.
        //
        // The event is process-global and xUnit runs classes in parallel, so
        // only this test's own sentinel is counted. Several rounds are run
        // because the event fires on FINALIZATION, which one collection does
        // not reliably reach.
        const string Sentinel = "learnstack-cache-unobserved-probe";
        var mine = new ConcurrentBag<Exception>();

        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions
                .Any(inner => inner.Message == Sentinel))
            {
                mine.Add(e.Exception);
                e.SetObserved();
            }
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            for (var round = 0; round < 10; round++)
            {
                var (cache, _) = New();
                using var fail = new SemaphoreSlim(0);
                using var abandoning = new CancellationTokenSource();
                var entered = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                var abandoned = cache.GetOrSetAsync<string>(Key(), async _ =>
                {
                    entered.TrySetResult();
                    await fail.WaitAsync(TestTimeout, CancellationToken.None);
                    throw new InvalidOperationException(Sentinel);
                }, null, abandoning.Token);

                await entered.Task.WaitAsync(TestTimeout);
                await abandoning.CancelAsync();
                await ((Func<Task>)(() => abandoned))
                    .Should().ThrowAsync<OperationCanceledException>();

                fail.Release();
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            for (var collection = 0; collection < 4; collection++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            mine.Should().BeEmpty("the flight observes its own fault");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task A_Finished_Flight_Is_Unregistered()
    {
        // The other half of the same rule: binding cleanup to the flight rather
        // than to a caller must not mean never cleaning up.
        var (cache, _) = New();

        await cache.GetOrSetAsync(Key(), _ => Task.FromResult("value"));

        cache.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task Nothing_Unbounded_Survives_A_Large_Key_Space()
    {
        // Measured on the first version: a per-key version counter lived in a
        // dictionary of its own that nothing ever swept, so _entries held its
        // 10,000 ceiling while that map held all 50,000 — an unbounded
        // structure hiding behind a bounded one, reached by ordinary per-entity
        // keys rather than by misuse. The counter is now a flag on the flight,
        // which dies with it, so there is no second map to grow.
        var (cache, _) = New();
        var ttl = new CacheOptions(L1Ttl: TimeSpan.FromDays(30));

        for (var i = 0; i < InMemoryCacheService.MaxEntries * 5; i++)
        {
            await cache.GetOrSetAsync(
                CacheKey.ForTenant(Tenant, "tenancy", $"k{i:D6}"),
                _ => Task.FromResult(i),
                ttl);
        }

        cache.Count.Should().BeLessThanOrEqualTo(InMemoryCacheService.MaxEntries);
        cache.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task Two_Types_On_One_Key_Each_Get_Their_Own_Factory()
    {
        // A flight hands its result to every joiner, so keying it by the cache
        // key alone made two callers asking for different types share one run:
        // measured, the second caller's factory was never invoked and it
        // received the first caller's payload, which then threw on the cast.
        // Reusing one key for two types is a caller bug, but the cache must not
        // answer it by silently skipping a factory.
        var (cache, _) = New();
        using var release = new SemaphoreSlim(0);
        var textEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var text = cache.GetOrSetAsync(Key(), async _ =>
        {
            textEntered.TrySetResult();
            await release.WaitAsync(TestTimeout, CancellationToken.None);
            return "text";
        });

        await textEntered.Task.WaitAsync(TestTimeout);
        var number = cache.GetOrSetAsync(Key(), _ => Task.FromResult(42));

        (await number).Should().Be(42, "its own factory ran");
        release.Release();
        (await text).Should().Be("text");
    }

    [Fact]
    public async Task A_Key_Holding_Another_Type_Reads_As_A_Miss()
    {
        var (cache, _) = New();
        await cache.SetAsync(Key(), "text");

        (await cache.GetAsync<int?>(Key())).Should().BeNull(
            "a miss lets the caller read the source of truth; a cast would throw "
            + "out of a component whose contract is that a miss is never an error");
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
            await cache.SetAsync(CacheKey.ForTenant(Tenant, "tenancy", $"k{i}"), i);
        }

        cache.Count.Should().Be(200);

        clock.Advance(InMemoryCacheService.DefaultTtl + InMemoryCacheService.SweepInterval);
        await cache.GetAsync<int?>(CacheKey.ForTenant(Tenant, "tenancy", "trigger"));

        cache.Count.Should().Be(0, "a sweep reclaims what expired, bound or no bound");
    }

    [Fact]
    public async Task GetOrSet_Recomputes_After_Its_Value_Expires()
    {
        // GetOrSetAsync has its own hit check, separate from GetAsync's, and it
        // is the one a caller reaches for on the hot path. Without this, a
        // regression that served the first value forever would ship green.
        var (cache, clock) = New();
        var runs = 0;

        Task<string> Factory(CancellationToken _)
        {
            runs++;
            return Task.FromResult($"run-{runs}");
        }

        // A TTL shorter than the sweep interval, and a step that stays inside
        // that interval: the entry is expired but the sweep is still throttled,
        // so GetOrSetAsync's own freshness check is the only thing standing
        // between the caller and a stale value. Step further and the sweep
        // reclaims it first, which is why an earlier version of this test could
        // not tell a missing check from a working one.
        var brief = new CacheOptions(L1Ttl: TimeSpan.FromMilliseconds(300));

        (await cache.GetOrSetAsync(Key(), Factory, brief)).Should().Be("run-1");
        (await cache.GetOrSetAsync(Key(), Factory, brief)).Should().Be("run-1", "still fresh");

        clock.Advance(TimeSpan.FromMilliseconds(600));

        (await cache.GetOrSetAsync(Key(), Factory, brief)).Should().Be("run-2",
            "expired, and no sweep is due to have removed it");
    }

    [Fact]
    public async Task A_Clock_That_Steps_Backwards_Does_Not_Wedge_The_Sweep()
    {
        // The throttle compares tick deltas, so a clock that jumps backwards —
        // an NTP correction, a leap adjustment — would otherwise park the sweep
        // until real time caught up to the future value it recorded. Every
        // other test only moves the clock forward, so the guard against it had
        // no coverage at all.
        var (cache, clock) = New();
        clock.Advance(TimeSpan.FromHours(6));
        await cache.SetAsync(Key(), "value");

        clock.SetUtcNow(Origin);

        for (var i = 0; i < 200; i++)
        {
            await cache.SetAsync(CacheKey.ForTenant(Tenant, "tenancy", $"k{i}"), i);
        }

        clock.Advance(InMemoryCacheService.DefaultTtl + InMemoryCacheService.SweepInterval);
        await cache.GetAsync<int?>(CacheKey.ForTenant(Tenant, "tenancy", "trigger"));

        // 200 reclaimed; the one written six hours ahead is still genuinely
        // fresh at this instant, so it stays. Without the backwards guard the
        // sweep would record the future timestamp and refuse to run until real
        // time passed it — leaving all 201.
        cache.Count.Should().Be(1, "the sweep still runs after the clock steps back");
    }

    [Fact]
    public async Task The_Bound_Reclaims_Expired_Entries_Before_Evicting_Live_Ones()
    {
        // Trim has two passes and only the second had coverage: the bound test
        // uses a 30-day TTL, so nothing is ever expired when Trim runs. An
        // eviction that drops a live entry while an expired one sits next to it
        // costs a round trip that nothing was owed.
        // The two passes only differ when an expired entry is NEWER than a live
        // one — otherwise evicting by insertion order removes the expired ones
        // anyway, and dropping the first pass changes nothing observable. So the
        // live entries go in FIRST and the doomed ones after them.
        var (cache, clock) = New();
        var live = new CacheOptions(L1Ttl: TimeSpan.FromDays(30));
        var half = InMemoryCacheService.MaxEntries / 2;

        for (var i = 0; i < half; i++)
        {
            await cache.SetAsync(CacheKey.ForTenant(Tenant, "tenancy", $"live{i:D6}"), i, live);
        }

        var brief = new CacheOptions(L1Ttl: TimeSpan.FromMilliseconds(300));
        for (var i = 0; i < half; i++)
        {
            await cache.SetAsync(CacheKey.ForTenant(Tenant, "tenancy", $"doomed{i}"), i, brief);
        }

        // Inside the sweep interval, so the doomed entries are expired but still
        // occupying slots when Trim runs — which is the whole point.
        clock.Advance(TimeSpan.FromMilliseconds(600));

        // `half`, not `half + 1`: 5,000 live plus 5,000 fresh is exactly the
        // ceiling, so once the expired ones are reclaimed nothing live has to
        // go. One more and the oldest live entry is evicted legitimately, which
        // would make this test fail for a reason it is not about.
        for (var i = 0; i < half; i++)
        {
            await cache.SetAsync(CacheKey.ForTenant(Tenant, "tenancy", $"fresh{i:D6}"), i, live);
        }

        (await cache.GetAsync<int?>(CacheKey.ForTenant(Tenant, "tenancy", "live000000")))
            .Should().Be(0,
                "the oldest LIVE entry survives, because expired ones are "
                + "reclaimed before anything live is evicted for space");
    }

    [Fact]
    public async Task The_Sweep_Runs_At_Most_Once_Per_Interval()
    {
        // The throttle is documented behaviour, and over-sweeping is correct but
        // wasteful — so it is asserted rather than assumed.
        var (cache, clock) = New();
        await cache.SetAsync(Key(), "value", new CacheOptions(L1Ttl: TimeSpan.FromMilliseconds(300)));
        await cache.SetAsync(CacheKey.ForTenant(Tenant, "tenancy", "other"), 1);

        // Past the entry's TTL, but inside the interval since the last sweep.
        clock.Advance(TimeSpan.FromMilliseconds(600));
        (await cache.GetAsync<string>(Key())).Should().BeNull("expired entries are never served");
        cache.Count.Should().Be(2, "but no sweep is due, so the slot is not reclaimed yet");

        clock.Advance(InMemoryCacheService.SweepInterval);
        await cache.GetAsync<string>(Key());
        cache.Count.Should().Be(1, "now a sweep is due");
    }

    [Fact]
    public async Task An_Explicitly_Stored_Null_Reads_Back_As_A_Miss()
    {
        // Pinned rather than fixed: `T?` cannot distinguish "stored null" from
        // "absent", and inventing a wrapper to tell them apart would complicate
        // every call site for a distinction none of them makes. The cost is one
        // occupied slot, which the bound already accounts for.
        var (cache, _) = New();

        await cache.SetAsync<string?>(Key(), null);

        (await cache.GetAsync<string?>(Key())).Should().BeNull();
        cache.Count.Should().Be(1, "it does occupy a slot, whatever a reader sees");
    }

    [Fact]
    public async Task A_Factory_That_Outlives_Its_Budget_Times_Out_Rather_Than_Cancels()
    {
        // The service owns a 30s factory budget, and it used to end the flight by
        // CANCELLING it — so every waiter, including ones whose own token was
        // perfectly healthy, was told "you asked for this" when they had not.
        // A caller could not tell its own cancellation from the cache's budget
        // expiring, and ASP.NET reads a cancellation as "the client hung up":
        // no body, no captured error, no span, so a timeout an operator needs to
        // see would vanish.
        var cache = new InMemoryCacheService(
            new FixedClock(Origin), MeterFactory, factoryTimeout: TimeSpan.FromMilliseconds(80));
        using var never = new SemaphoreSlim(0);

        var act = () => cache.GetOrSetAsync(
            Key(),
            async token =>
            {
                await never.WaitAsync(TimeSpan.FromMinutes(5), token);
                return "never";
            });

        // A TimeoutException, not an OperationCanceledException: the caller's own
        // token was never cancelled, so a cancellation would be a lie about who
        // gave up.
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task A_Factory_That_Ignores_Its_Token_Is_Still_Bounded()
    {
        // The budget used to be a token and nothing else, so it only worked on
        // factories that observed it — and a dependency call that does not
        // thread one is the ordinary case, not the exotic one. Measured against
        // a 150 ms budget: the caller waited 3,002 ms and was handed the late
        // value. The deadline is raced now.
        var cache = new InMemoryCacheService(
            new FixedClock(Origin), MeterFactory, factoryTimeout: TimeSpan.FromMilliseconds(80));
        var runs = 0;

        var overrunning = cache.GetOrSetAsync<string>(Key(), async _ =>
        {
            Interlocked.Increment(ref runs);
            await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);
            return "late";
        });

        await ((Func<Task>)(() => overrunning)).Should().ThrowAsync<TimeoutException>();

        // The replacement waits for the overrunning factory rather than starting
        // a second one beside it, and is not answered with the first caller's
        // timeout for ever.
        var replacement = await cache.GetOrSetAsync(Key(), _ => Task.FromResult("fresh"))
            .WaitAsync(TestTimeout);

        replacement.Should().Be("fresh");
        runs.Should().Be(1, "no second factory ran alongside the first");
        (await cache.GetAsync<string>(Key())).Should().Be("fresh",
            "a result that arrived after its deadline is not cached");
    }

    [Theory]
    [InlineData(0, "zero cancels immediately, so every factory times out at once")]
    [InlineData(-5000, "a negative span throws from Flight on the first miss, not here")]
    [InlineData(-1, "Timeout.InfiniteTimeSpan never fires — a budget that is not one")]
    public void A_Timeout_That_Is_Not_A_Timeout_Is_Refused_At_Construction(
        int milliseconds, string why)
    {
        // CancelAfter answers these three differently and none of them at the
        // wiring that was wrong: zero is accepted and fires instantly, a negative
        // throws from inside the first flight, and InfiniteTimeSpan is accepted
        // and never fires at all — the deadline silently not existing, which is
        // the defect the raced budget removed, reached through configuration.
        var act = () => new InMemoryCacheService(
            new FixedClock(Origin),
            MeterFactory,
            factoryTimeout: TimeSpan.FromMilliseconds(milliseconds));

        act.Should().Throw<ArgumentOutOfRangeException>(why)
            .And.ParamName.Should().Be("factoryTimeout");
    }

    [Fact]
    public void A_Positive_Timeout_And_The_Default_Are_Both_Accepted()
    {
        var explicitly = () => new InMemoryCacheService(
            new FixedClock(Origin), MeterFactory, factoryTimeout: TimeSpan.FromMilliseconds(1));
        var byDefault = () => new InMemoryCacheService(new FixedClock(Origin), MeterFactory);

        explicitly.Should().NotThrow();
        byDefault.Should().NotThrow();
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

    [Fact]
    public async Task A_Write_During_The_Atomic_Miss_Check_Is_Observed()
    {
        InMemoryCacheService? cache = null;
        var clock = new WritingClock(Origin, onNthRead: 2, write: () =>
            cache!.SetAsync(Key(), "landed-in-the-window").GetAwaiter().GetResult());
        cache = new InMemoryCacheService(clock, MeterFactory);
        var calls = 0;

        var produced = await cache.GetOrSetAsync(Key(), _ =>
        {
            calls++;
            return Task.FromResult("from-factory");
        });

        produced.Should().Be("landed-in-the-window");
        calls.Should().Be(0, "the write landed before a flight could be published");
        clock.Fired.Should().BeTrue("the window was actually hit — otherwise this proves nothing");
        (await cache.GetAsync<string>(Key())).Should().Be("landed-in-the-window");
    }

    /// <summary>
    /// Raises <paramref name="maximum"/> to <paramref name="candidate"/> if it is
    /// higher, atomically.
    /// </summary>
    /// <remarks>
    /// <c>Interlocked.Exchange(ref max, Math.Max(max, current))</c> reads, computes
    /// and writes as three separate steps: two threads can both read the same
    /// value and the lower result can land last, losing a genuine overlap. In a
    /// test whose whole point is detecting overlap, that is a guard that passes
    /// on broken code.
    /// </remarks>
    private static void RecordMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);

        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    /// <summary>
    /// A clock that performs a write on one chosen read, to land a concurrent
    /// operation inside a window too narrow to hit by scheduling.
    /// </summary>
    private sealed class WritingClock(DateTimeOffset now, int onNthRead, Action write) : IClock
    {
        private int _reads;

        public bool Fired { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                if (++_reads == onNthRead && !Fired)
                {
                    Fired = true;
                    write();
                }

                return now;
            }
        }
    }

    private static (InMemoryCacheService Cache, FixedClock Clock) New()
    {
        var clock = new FixedClock(Origin);
        return (new InMemoryCacheService(clock, MeterFactory), clock);
    }
}

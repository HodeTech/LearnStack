using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.MultiTenancy;

/// <summary>
/// The separately-capped structure
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// requires for unknown hosts, so a flood cannot evict real mappings.
/// </summary>
/// <remarks>
/// The requirement is two words — "separately capped" — and both halves are load
/// bearing. <b>Separate</b>, because the shared <c>ICacheService</c> is one
/// process-wide pool trimmed oldest-first across every family, so unknown hosts
/// routed through it would age out the mappings they are supposed to protect.
/// <b>Capped</b>, because every miss is a PostgreSQL transaction on an anonymous
/// pre-authentication path and an uncapped memo of every hostname ever guessed is
/// its own denial of service.
/// </remarks>
public sealed class UnknownHostCacheTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_Unseen_Host_Is_Not_Remembered()
    {
        Build(out _).Contains("never.example.com").Should().BeFalse();
    }

    [Fact]
    public void A_Recorded_Host_Is_Remembered()
    {
        var cache = Build(out _);

        cache.Add("gone.example.com");

        cache.Contains("gone.example.com").Should().BeTrue();
    }

    [Fact]
    public void A_Recorded_Host_Is_Forgotten_When_Its_Answer_Expires()
    {
        // The backstop, not the mechanism: activation calls Forget, and this is
        // what keeps a host that went live without one from being denied for the
        // life of the process.
        var cache = Build(out var clock, new UnknownHostCacheOptions { Ttl = TimeSpan.FromMinutes(2) });
        cache.Add("later.example.com");

        clock.Advance(TimeSpan.FromMinutes(2));

        cache.Contains("later.example.com").Should().BeFalse();
        cache.Count.Should().Be(0, "an expired entry is dropped on the read that found it");
    }

    [Fact]
    public void Forget_Removes_A_Host_Immediately()
    {
        // What the activation path calls. Without it, a hostname guessed before it
        // went live keeps its 404 for the whole TTL after activation — the cache
        // window ADR-0036 asks to be closed on the transaction that flips either
        // flag.
        var cache = Build(out _);
        cache.Add("activated.example.com");

        cache.Forget("activated.example.com");

        cache.Contains("activated.example.com").Should().BeFalse();
    }

    [Fact]
    public void A_Flood_Cannot_Grow_The_Structure_Past_Its_Cap()
    {
        var cache = Build(out _, new UnknownHostCacheOptions { MaxEntries = 50 });

        for (var i = 0; i < 500; i++)
        {
            cache.Add($"flood-{i}.example.com");
        }

        cache.Count.Should().BeLessThanOrEqualTo(50,
            "the cap is what stops a flood of novel hostnames becoming an unbounded memo");
    }

    [Fact]
    public void A_Flood_Evicts_The_Oldest_Unknown_Hosts_And_Nothing_Else()
    {
        // The property the separation buys: whatever a flood costs, it costs only
        // other unknown hosts. Real mappings live in ICacheService and are not
        // reachable from here at all — which is the point, and is why this asserts
        // on which unknown host survives rather than on a mapping.
        var cache = Build(out var clock, new UnknownHostCacheOptions { MaxEntries = 10 });

        cache.Add("oldest.example.com");
        clock.Advance(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 100; i++)
        {
            cache.Add($"flood-{i}.example.com");
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        cache.Contains("oldest.example.com").Should().BeFalse(
            "oldest-first is what a bounded structure evicts by");
        cache.Contains("flood-99.example.com").Should().BeTrue(
            "the most recent answer is the one worth keeping");
    }

    [Fact]
    public void A_Trim_Leaves_Headroom_Rather_Than_Stopping_At_The_Cap()
    {
        // Measured: trimming back to the cap itself passes every other case here,
        // including both flood cases, because none of them asserts the count is
        // strictly BELOW the cap. Without the headroom the map sits one add from
        // overflowing and every subsequent novel host pays for another full sort —
        // the property the code's own comment claims and nothing checked.
        //
        // Exactly one add past the cap, not a flood: a flood lands wherever the
        // remainder leaves it, and the number this pins is the target itself.
        const int Cap = 100;
        var cache = Build(out _, new UnknownHostCacheOptions
        {
            MaxEntries = Cap,
            Ttl = TimeSpan.FromHours(1),
        });

        for (var i = 0; i <= Cap; i++)
        {
            cache.Add($"host-{i}.example.com");
        }

        cache.Count.Should().Be(Cap * 9 / 10,
            "one trim goes to the low-water mark, not to the cap");
    }

    [Fact]
    public async Task Concurrent_Adds_At_The_Cap_Do_Not_Throw()
    {
        // The blocker this case exists for was measured, not imagined: the first
        // Trim enumerated the dictionary through LINQ, which buffers via
        // ICollection.CopyTo after a stale Count read. Eight threads adding at the
        // cap threw on 33% of adds — ArgumentException from a concurrent insert,
        // ArgumentNullException from a default slot left by a concurrent removal.
        // Add is unguarded in the resolver and the middleware has no catch, so
        // each throw was a 500 where a bodyless 404 was designed — and, because
        // only an unknown host reaches Add, a positive host-existence oracle.
        var cache = Build(out _, new UnknownHostCacheOptions { MaxEntries = 200 });

        for (var i = 0; i < 200; i++)
        {
            cache.Add($"seed-{i}.example.com");
        }

        using var release = new ManualResetEventSlim(false);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            release.Wait();

            for (var i = 0; i < 200; i++)
            {
                try
                {
                    cache.Add($"w{worker}-{i}.example.com");
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
        })).ToArray();

        release.Set();
        await Task.WhenAll(workers);

        failures.Should().BeEmpty(
            "Add is called from an unguarded, unauthenticated path — a throw here is a 500");
        cache.Count.Should().BeLessThanOrEqualTo(200, "the cap still bounds growth");
    }

    [Fact]
    public void A_Trim_Reclaims_The_Entries_That_Have_Lapsed()
    {
        // Nothing else sweeps: a read drops only the entry it looked at, so a map
        // that filled slowly would otherwise ratchet to its cap and stay there for
        // the life of the process, sorting the whole structure on every novel
        // host. The trim already pays for one pass; expiring on the way past is
        // free.
        var cache = Build(out var clock, new UnknownHostCacheOptions
        {
            MaxEntries = 100,
            Ttl = TimeSpan.FromMinutes(2),
        });

        for (var i = 0; i < 100; i++)
        {
            cache.Add($"lapsed-{i}.example.com");
        }

        clock.Advance(TimeSpan.FromMinutes(3));
        cache.Add("fresh.example.com");

        cache.Count.Should().Be(1,
            "one add past the cap sweeps every lapsed entry, leaving only the fresh one");
    }

    private static UnknownHostCache Build(
        out FixedClock clock, UnknownHostCacheOptions? options = null)
    {
        clock = new FixedClock(Origin);
        return new UnknownHostCache(clock, options ?? new UnknownHostCacheOptions());
    }
}

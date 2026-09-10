using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

/// <summary>
/// The anonymous half of the cross-tenant detector: when a run becomes a burst.
/// </summary>
/// <remarks>
/// An anonymous caller can generate mismatches at will, so a row per occurrence would let
/// that caller choose how much a tenant's audit log grows —
/// <see href="../../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Recording a rejected assertion</see> audits the burst instead, once per
/// <c>(resolved tenant, dimension, window)</c>. Every case here is about the "once".
/// </remarks>
public sealed class TenantAssertionBurstDetectorTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    [Fact]
    public void A_run_below_the_threshold_is_not_a_burst()
    {
        // Nine of ten. One stale header from a misconfigured BFF is a mistake, not a
        // probe, and a detector that fired on it would train an operator to ignore it.
        var detector = Detector(threshold: 10, out _);

        for (var attempt = 0; attempt < 9; attempt++)
        {
            detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant)
                .Should().BeFalse($"attempt {attempt + 1} is below the threshold");
        }
    }

    [Fact]
    public void The_crossing_is_the_occurrence_that_reaches_the_threshold()
    {
        var detector = Detector(threshold: 3, out _);

        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse();
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse();
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeTrue(
            "the third occurrence is the third, not the fourth — an off-by-one here is an "
            + "audit row that arrives one probe late, every time");
    }

    [Fact]
    public void Nothing_past_the_crossing_crosses_again_inside_the_window()
    {
        // ONE row per (tenant, dimension, window). A detector that kept answering true
        // would write a row per request for the rest of the window, which is the flood the
        // burst event exists to replace.
        var detector = Detector(threshold: 2, out _);

        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse();
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeTrue();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant)
                .Should().BeFalse("the window has already been recorded");
        }
    }

    [Fact]
    public void A_new_window_crosses_again()
    {
        // The window is reclaimed by EXPIRING and by nothing else, so a run that resumes
        // after the window is a new finding — the alternative is a detector that reports a
        // tenant once and then never again for the life of the process.
        var detector = Detector(threshold: 2, out var clock, window: TimeSpan.FromMinutes(5));

        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse();
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(5));

        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse(
            "the new window starts at one");
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeTrue();
    }

    [Fact]
    public void A_run_that_straddles_a_window_boundary_does_not_carry_its_count_over()
    {
        // The count belongs to the window. Carrying it over would make the threshold mean
        // "since the process started", which crosses once and never describes a rate.
        var detector = Detector(threshold: 3, out var clock, window: TimeSpan.FromMinutes(5));

        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse();
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse();

        clock.Advance(TimeSpan.FromMinutes(6));

        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse();
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse(
            "two carried over would have crossed here");
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeTrue();
    }

    [Fact]
    public void Each_tenant_and_each_dimension_counts_alone()
    {
        // The key is (resolved tenant, dimension). A shared counter would let traffic
        // against one tenant cross the threshold for another — a detector that names the
        // wrong tenant in a security event is worse than one that stays quiet.
        var detector = Detector(threshold: 2, out _);

        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeFalse();
        detector.RecordAndCheckCrossing(TenantB, TenantAssertionDimension.Tenant).Should().BeFalse();
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Organization).Should().BeFalse();

        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant).Should().BeTrue(
            "tenant A's tenant-dimension run reached two on its own");
        detector.RecordAndCheckCrossing(TenantB, TenantAssertionDimension.Tenant).Should().BeTrue();
        detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Organization).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_occurrences_cross_exactly_once()
    {
        // Two requests against one tenant must not both read count = threshold - 1 and
        // both cross, and must not lose an increment between them.
        //
        // The shape is deliberate. A long parallel run against a high threshold crosses
        // early, while contention is still low, and stays green with the lock removed —
        // measured. What trips it is many SHORT races, each with every caller released at
        // once onto a threshold two occurrences away: the two threads read the same count
        // and both write it back. Two hundred rounds of that fails on the first few
        // without the per-window lock.
        for (var round = 0; round < 200; round++)
        {
            var detector = Detector(threshold: 2, out _);
            var crossings = 0;

            using var gate = new Barrier(4);

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                gate.SignalAndWait();

                if (detector.RecordAndCheckCrossing(TenantA, TenantAssertionDimension.Tenant))
                {
                    Interlocked.Increment(ref crossings);
                }
            })));

            crossings.Should().Be(1, $"one window, one row — round {round}");
        }
    }

    [Fact]
    public void The_shipped_defaults_are_the_documented_ones()
    {
        // A threshold nobody configures is the threshold every deployment runs, so it is
        // worth pinning: ten in five minutes catches a run without reporting a mistake.
        var options = new AssertionBurstOptions();

        options.Threshold.Should().Be(10);
        options.Window.Should().Be(TimeSpan.FromMinutes(5));
    }

    private static TenantAssertionBurstDetector Detector(
        int threshold, out MovableClock clock, TimeSpan? window = null)
    {
        clock = new MovableClock(DateTimeOffset.UnixEpoch);

        return new TenantAssertionBurstDetector(
            Options.Create(new AssertionBurstOptions
            {
                Threshold = threshold,
                Window = window ?? TimeSpan.FromMinutes(5),
            }),
            clock);
    }

    /// <summary>A clock a case moves, so a window can expire without waiting for one.</summary>
    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}

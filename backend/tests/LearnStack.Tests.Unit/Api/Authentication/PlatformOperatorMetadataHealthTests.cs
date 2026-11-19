using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

/// <summary>
/// The memory the operator scheme keeps of its issuer metadata: when to refuse a fetch without
/// trying, and when to stop trusting keys a failed refresh could not renew (ADR-0101 § 2).
/// </summary>
public sealed class PlatformOperatorMetadataHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_Healthy_History_Is_Neither_In_Backoff_Nor_Stale()
    {
        var (health, clock) = Build();
        health.RecordSuccess();
        clock.Advance(TimeSpan.FromHours(3));

        health.InBackoff().Should().BeFalse();
        health.IsStale().Should().BeFalse("no refresh has failed, so the keys held are the issuer's latest known");
    }

    [Fact]
    public void A_Failure_Backs_Off_For_Thirty_Seconds_Then_Allows_A_Retry()
    {
        var (health, clock) = Build();
        health.RecordSuccess();
        clock.Advance(TimeSpan.FromMinutes(1));
        health.RecordFailure();

        health.InBackoff().Should().BeTrue();
        clock.Advance(PlatformOperatorMetadataHealth.RetryBackoff - TimeSpan.FromSeconds(1));
        health.InBackoff().Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(2));
        health.InBackoff().Should().BeFalse("the next request may try the issuer again");
    }

    [Fact]
    public void Keys_Become_Stale_Only_When_A_Later_Fetch_Failed_And_The_Last_Success_Is_Old()
    {
        var (health, clock) = Build();
        health.RecordSuccess();
        clock.Advance(PlatformOperatorMetadataHealth.MaximumStaleness + TimeSpan.FromMinutes(1));
        health.IsStale().Should().BeFalse("old but never failed since");

        health.RecordFailure();

        health.IsStale().Should().BeTrue();
    }

    [Fact]
    public void A_Failure_Within_The_Staleness_Window_Does_Not_Refuse_Tokens()
    {
        var (health, clock) = Build();
        health.RecordSuccess();
        clock.Advance(TimeSpan.FromMinutes(5));
        health.RecordFailure();

        health.IsStale().Should().BeFalse("a blip is bridged by the keys already held");
    }

    [Fact]
    public void A_Later_Success_Clears_Both()
    {
        var (health, clock) = Build();
        health.RecordSuccess();
        clock.Advance(PlatformOperatorMetadataHealth.MaximumStaleness + TimeSpan.FromMinutes(1));
        health.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(31));

        health.RecordSuccess();

        health.IsStale().Should().BeFalse();
        health.InBackoff().Should().BeFalse();
    }

    [Fact]
    public void A_Scheme_That_Never_Loaded_Keys_Is_Not_Called_Stale()
    {
        var (health, _) = Build();
        health.RecordFailure();

        health.IsStale().Should().BeFalse("with no keys held every token already fails, and for the right reason");
        health.InBackoff().Should().BeTrue();
    }

    private static (PlatformOperatorMetadataHealth Health, MovableClock Clock) Build()
    {
        var clock = new MovableClock(Start);
        return (new PlatformOperatorMetadataHealth(clock), clock);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}

using FluentAssertions;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Time;

public sealed class FixedClockTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 05, 21, 10, 00, 00, TimeSpan.Zero);

    [Fact]
    public void UtcNow_AfterConstruction_ReturnsConstructorValue()
    {
        var clock = new FixedClock(T0);

        clock.UtcNow.Should().Be(T0);
    }

    [Fact]
    public void Advance_AddsTheGivenInterval()
    {
        var clock = new FixedClock(T0);

        clock.Advance(TimeSpan.FromMinutes(30));

        clock.UtcNow.Should().Be(T0.AddMinutes(30));
    }

    [Fact]
    public void SetUtcNow_ReplacesTheCurrentInstant()
    {
        var clock = new FixedClock(T0);
        var target = T0.AddDays(7);

        clock.SetUtcNow(target);

        clock.UtcNow.Should().Be(target);
    }

    [Fact]
    public void Ctor_NormalisesNonUtcOffsetToUtc()
    {
        // 10:00 in +03:00 == 07:00 UTC. The IClock.UtcNow contract requires
        // a UTC-offset value; non-UTC inputs are normalised at the boundary.
        var nonUtc = new DateTimeOffset(2026, 05, 21, 10, 00, 00, TimeSpan.FromHours(3));

        var clock = new FixedClock(nonUtc);

        clock.UtcNow.Offset.Should().Be(TimeSpan.Zero);
        clock.UtcNow.Hour.Should().Be(7, "10:00 +03:00 is 07:00 UTC");
    }

    [Fact]
    public void SetUtcNow_NormalisesNonUtcOffsetToUtc()
    {
        var clock = new FixedClock(T0);
        var nonUtc = new DateTimeOffset(2026, 05, 21, 12, 00, 00, TimeSpan.FromHours(-5));

        clock.SetUtcNow(nonUtc);

        clock.UtcNow.Offset.Should().Be(TimeSpan.Zero);
        clock.UtcNow.Hour.Should().Be(17, "12:00 -05:00 is 17:00 UTC");
    }
}

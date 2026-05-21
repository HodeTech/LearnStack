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
}

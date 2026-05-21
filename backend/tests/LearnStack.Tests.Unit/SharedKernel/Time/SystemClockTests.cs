using FluentAssertions;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Time;

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_ReturnsCurrentInstant_WithinTolerance()
    {
        var clock = new SystemClock();

        var before = DateTimeOffset.UtcNow;
        var observed = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;

        observed.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        observed.Offset.Should().Be(TimeSpan.Zero, "SystemClock returns UTC");
    }
}

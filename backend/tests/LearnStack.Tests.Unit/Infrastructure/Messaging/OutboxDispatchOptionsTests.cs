using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

public sealed class OutboxDispatchOptionsTests
{
    [Fact]
    public void Approved_defaults_have_a_finite_publish_and_transition_margin()
    {
        var options = new OutboxDispatchOptions();
        options.Validate();
        options.LeaseDuration.Should().BeGreaterThan(options.PublishTimeout + options.SqlTimeout * 2);
        options.MaxOutstandingPublishes.Should().Be(4);
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("publish")]
    [InlineData("lease")]
    [InlineData("poll")]
    [InlineData("progress")]
    [InlineData("snapshot")]
    [InlineData("shutdown")]
    [InlineData("capacity")]
    public void Invalid_bounds_are_refused_before_runtime(string setting)
    {
        var options = new OutboxDispatchOptions();
        options = setting switch
        {
            "sql" => options with { SqlTimeout = TimeSpan.FromSeconds(6) },
            "publish" => options with { PublishTimeout = TimeSpan.Zero },
            "lease" => options with { LeaseDuration = TimeSpan.FromSeconds(20) },
            "poll" => options with { IdlePoll = Timeout.InfiniteTimeSpan },
            "progress" => options with { ProgressTimeout = TimeSpan.FromSeconds(46) },
            "snapshot" => options with { SnapshotInterval = TimeSpan.Zero },
            "shutdown" => options with { ShutdownTimeout = TimeSpan.FromSeconds(31) },
            "capacity" => options with { MaxOutstandingPublishes = 5 },
            _ => throw new ArgumentOutOfRangeException(nameof(setting)),
        };
        options.Invoking(value => value.Validate()).Should().Throw<ArgumentException>();
    }
}

using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobRuntimeOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_six_attempt_budgets_cannot_be_overridden_by_definitions()
    {
        new BackgroundJobRuntimeOptions().Validate();
        BackgroundJobRuntimeOptions.MaxNormalAttempts.Should().Be(6);
        BackgroundJobRuntimeOptions.MaxFinalizeAttempts.Should().Be(6);
        BackgroundJobRuntimeOptions.MaxUnsuccessfulRecoveryProbes.Should().Be(6);
        BackgroundJobRuntimeOptions.RetryDelays.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        var mutation = () => ((IList<TimeSpan>)BackgroundJobRuntimeOptions.RetryDelays)[0] = TimeSpan.Zero;
        mutation.Should().Throw<NotSupportedException>();
        BackgroundJobRuntimeOptions.RetainedHardBound(1).Should().Be(5);
        BackgroundJobRuntimeOptions.RetainedHardBound(16).Should().Be(20);
    }

    [Fact]
    public void Lease_must_strictly_exceed_two_renewals_two_sql_timeouts_and_five_seconds()
    {
        var defaults = new BackgroundJobRuntimeOptions();
        var boundary = defaults.RenewalInterval * 2 + defaults.SqlTimeout * 2 + TimeSpan.FromSeconds(5);
        var equality = () => (defaults with { LeaseDuration = boundary }).Validate();
        equality.Should().Throw<ArgumentException>();
        (defaults with { LeaseDuration = boundary + TimeSpan.FromTicks(1) }).Validate();
    }

    [Fact]
    public void Every_configurable_bound_rejects_its_out_of_range_control()
    {
        var defaults = new BackgroundJobRuntimeOptions();
        var invalid = new[]
        {
            defaults with { SqlTimeout = TimeSpan.Zero }, defaults with { SqlTimeout = TimeSpan.FromSeconds(6) },
            defaults with { ProbeTimeout = TimeSpan.Zero },
            defaults with { ProbeTimeout = TimeSpan.FromSeconds(31) },
            defaults with { TransactionTimeout = TimeSpan.Zero },
            defaults with { TransactionTimeout = TimeSpan.FromMinutes(6) },
            defaults with { ExternalWaitTimeout = TimeSpan.Zero },
            defaults with { ExternalWaitTimeout = TimeSpan.FromHours(25) },
            defaults with { LeaseDuration = TimeSpan.FromSeconds(14) }, defaults with { LeaseDuration = TimeSpan.FromMinutes(11) },
            defaults with { RenewalInterval = TimeSpan.Zero }, defaults with { RenewalInterval = TimeSpan.FromSeconds(61) },
            defaults with { QuiescenceGrace = TimeSpan.Zero },
            defaults with { QuiescenceGrace = TimeSpan.FromSeconds(31) },
            defaults with { ControlPoll = TimeSpan.FromMilliseconds(49) }, defaults with { ControlPoll = TimeSpan.FromSeconds(2) },
            defaults with { SnapshotInterval = TimeSpan.Zero }, defaults with { SnapshotInterval = TimeSpan.FromSeconds(61) },
            defaults with { HandoffRepairGrace = TimeSpan.FromSeconds(14) }, defaults with { HandoffRepairGrace = TimeSpan.FromSeconds(301) },
            defaults with { RecoveryProbeInterval = TimeSpan.FromSeconds(4) }, defaults with { RecoveryProbeInterval = TimeSpan.FromSeconds(301) },
            defaults with { ShutdownTimeout = TimeSpan.Zero },
            defaults with { ShutdownTimeout = TimeSpan.FromSeconds(31) },
            defaults with { ProgressTimeout = TimeSpan.FromSeconds(74) }, defaults with { ProgressTimeout = TimeSpan.FromMinutes(16) },
            defaults with { BridgeWaitTimeout = TimeSpan.Zero },
            defaults with { BridgeWaitTimeout = TimeSpan.FromSeconds(11) },
            defaults with { ControlBatchSize = 0 }, defaults with { ControlBatchSize = 101 },
            defaults with { FanoutPageSize = 0 }, defaults with { FanoutPageSize = 101 },
        };
        foreach (var options in invalid)
        {
            var action = () => options.Validate();
            action.Should().Throw<ArgumentException>();
        }
    }
}

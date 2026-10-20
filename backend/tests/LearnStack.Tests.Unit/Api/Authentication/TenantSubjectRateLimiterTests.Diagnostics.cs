using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Api.Authentication;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed partial class TenantSubjectRateLimiterTests
{
    [Fact]
    public async Task Throwing_fault_metric_listener_cannot_abandon_retirement_or_shared_shutdown()
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(2);
            budgets.Add(budget);
            return budget;
        }, Window, clock, maximumPartitions: 3);
        for (var index = 0; index < 3; index++) owner.AttemptAcquire(Identity($"subject-{index}"));
        budgets[0].Replenish(Window);
        budgets[1].Replenish(Window);
        var disposalFailure = new InvalidOperationException("synthetic disposal failure");
        var listenerFailure = new InvalidOperationException("synthetic metric listener failure");
        budgets[0].OnDispose = () => throw disposalFailure;
        var thread = Environment.CurrentManagedThreadId;
        var notifications = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observed) =>
        {
            if (instrument.Meter.Name == TenantSubjectAdmissionMetrics.MeterName
                && instrument.Name == TenantSubjectAdmissionMetrics.CleanupFailedCounterName)
                observed.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (Environment.CurrentManagedThreadId != thread) return;
            notifications++;
            throw listenerFailure;
        });
        listener.Start();

        try
        {
            clock.Timer.Fire().Should().BeTrue("a diagnostic callback cannot escape owned retirement");
            notifications.Should().Be(1);
            budgets[1].CompletedDisposals.Should().Be(1, "the entire already-owned batch must be attempted");
            owner.AttemptAcquire(Identity("unseen")).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryFault);
            owner.AttemptAcquire(Identity("subject-2")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
        }
        finally
        {
            var shutdown = owner.DisposeAsync().AsTask();
            var again = owner.DisposeAsync().AsTask();
            again.Should().BeSameAs(shutdown);
            var failure = await Record.ExceptionAsync(() => shutdown.WaitAsync(Timeout));
            failure.Should().BeOfType<AggregateException>().Which.Flatten().InnerExceptions
                .Should().BeEquivalentTo(new[] { disposalFailure, listenerFailure });
        }
        budgets.Should().OnlyContain(budget => budget.Disposals == 1);
        budgets[2].CompletedDisposals.Should().Be(1, "shutdown still drains retained keys");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timer_join_failure_remains_observed_while_both_shutdown_forms_drain_retained_keys(bool asynchronous)
    {
        using var clock = new ManualClock();
        var budget = new TrackedBudget(1);
        var owner = new TenantSubjectRateLimiter(() => budget, Window, clock);
        owner.AttemptAcquire(Identity());
        var timerFailure = new InvalidOperationException("synthetic timer join failure");
        clock.Timer.DisposalFailure = timerFailure;
        var failure = await Record.ExceptionAsync(async () =>
        {
            if (asynchronous) await owner.DisposeAsync().AsTask().WaitAsync(Timeout);
            else await Task.Run(owner.Dispose).WaitAsync(Timeout);
        });
        failure.Should().BeOfType<AggregateException>().Which.Flatten().InnerExceptions
            .Should().ContainSingle().Which.Should().BeSameAs(timerFailure);
        budget.Disposals.Should().Be(1);
        budget.CompletedDisposals.Should().Be(1);
        budget.AsyncDisposals.Should().Be(asynchronous ? 1 : 0);
        var again = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask().WaitAsync(Timeout));
        again.Should().BeSameAs(failure);
        clock.Timer.AsyncDisposals.Should().Be(1);
        budget.Disposals.Should().Be(1);
    }
}

using System.Collections.Concurrent;
using FluentAssertions;
using LearnStack.Api.Authentication;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed partial class TenantSubjectRateLimiterTests
{
    [Fact]
    public async Task Blocked_timer_owns_one_128_resource_batch_and_allows_128_slot_refills_with_4224_total_resources()
    {
        using var clock = new ManualClock();
        ConcurrentQueue<TrackedBudget> budgets = new();
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Enqueue(budget);
            return budget;
        }, Window, clock);
        for (var index = 0; index < 4096; index++) owner.AttemptAcquire(Identity($"original-{index}"));
        foreach (var budget in budgets.Take(128)) budget.Replenish(Window);
        var entered = Completion();
        using var release = new ManualResetEventSlim();
        budgets.First().OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var sweep = Task.Run(clock.Timer.Fire);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            for (var index = 0; index < 128; index++)
                owner.AttemptAcquire(Identity($"replacement-{index}")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            budgets.Should().HaveCount(4224);
            budgets.Sum(budget => budget.CompletedDisposals).Should().Be(0,
                "removal and a started Dispose are not evidence that resources were released");

            (await Task.Run(() => owner.AttemptAcquire(Identity("overflow"))).WaitAsync(Timeout))
                .Outcome.Should().Be(SubjectAdmissionOutcome.RegistryCapacity);
            await Task.Run(clock.Timer.Fire).WaitAsync(Timeout);
            budgets.Sum(budget => budget.IdleReads).Should().Be(128,
                "a blocked timer batch excludes both another timer scan and acquisition reclamation");
            budgets.Should().HaveCount(4224);
            owner.AttemptAcquire(Identity("original-128")).Outcome.Should().Be(SubjectAdmissionOutcome.QuotaExhausted,
                "unretired live quota remains spent while slots are reused");
        }
        finally
        {
            release.Set();
            try { await sweep.WaitAsync(Timeout); }
            finally { await owner.DisposeAsync(); }
        }

        budgets.Should().OnlyContain(budget => budget.Disposals == 1 && budget.CompletedDisposals == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_shutdown_forms_join_acquisition_cleanup_and_preserve_detached_decisions(bool asynchronous)
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Add(budget);
            return budget;
        }, Window, clock, maximumPartitions: 2);
        var admitted = owner.AttemptAcquire(Identity("idle"));
        var retained = owner.AttemptAcquire(Identity("existing"));
        var refused = owner.AttemptAcquire(Identity("existing"));
        budgets[0].Replenish(Window);
        var entered = Completion();
        using var release = new ManualResetEventSlim();
        budgets[0].OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var acquisition = Task.Run(() => owner.AttemptAcquire(Identity("new-key")));
        Task shutdown = Task.CompletedTask;
        Task concurrentShutdown = Task.CompletedTask;
        Exception? acquisitionFailure = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            shutdown = Task.Run(async () =>
            {
                if (asynchronous) await owner.DisposeAsync();
                else owner.Dispose();
            });
            await clock.Timer.DisposalRequested.Task.WaitAsync(Timeout);
            concurrentShutdown = owner.DisposeAsync().AsTask();
            shutdown.IsCompleted.Should().BeFalse();
            concurrentShutdown.IsCompleted.Should().BeFalse();
            acquisition.IsCompleted.Should().BeFalse();
            budgets.Should().HaveCount(2);
            var afterClose = () => owner.AttemptAcquire(Identity("existing"));
            afterClose.Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            release.Set();
            try
            {
                acquisitionFailure = await Record.ExceptionAsync(async () => await acquisition.WaitAsync(Timeout));
                await Task.WhenAll(shutdown, concurrentShutdown).WaitAsync(Timeout);
            }
            finally { await owner.DisposeAsync(); }
        }

        acquisitionFailure.Should().BeOfType<ObjectDisposedException>();
        budgets.Should().HaveCount(2).And.OnlyContain(budget => budget.Disposals == 1);
        budgets[1].AsyncDisposals.Should().Be(asynchronous ? 1 : 0);
        admitted.Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
        retained.Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
        refused.Outcome.Should().Be(SubjectAdmissionOutcome.QuotaExhausted);
        refused.RetryAfter.Should().Be(Window);
        budgets.SelectMany(budget => budget.Leases).Should().OnlyContain(lease => lease.Disposals == 1);
        clock.Timer.AsyncDisposals.Should().Be(1);
    }
}

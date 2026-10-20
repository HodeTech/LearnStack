using System.Collections.Concurrent;
using FluentAssertions;
using LearnStack.Api.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed partial class TenantSubjectRateLimiterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_service_provider_shutdown_observes_singleton_cleanup_failure_and_closes_admission(bool asynchronous)
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Add(budget);
            return budget;
        }, Window, clock, maximumPartitions: 2));
        services.AddLearnStackTenantSubjectAdmission();
        services.Single(descriptor => descriptor.ServiceType == typeof(TenantSubjectRateLimiter))
            .Lifetime.Should().Be(ServiceLifetime.Singleton);
        var provider = services.BuildServiceProvider();
        var owner = provider.GetRequiredService<TenantSubjectRateLimiter>();
        owner.Should().BeSameAs(provider.GetRequiredService<TenantSubjectRateLimiter>());
        var failure = new InvalidOperationException("synthetic singleton retirement failure");
        try
        {
            owner.AttemptAcquire(Identity("retiring")).IsAcquired.Should().BeTrue();
            owner.AttemptAcquire(Identity("retained")).IsAcquired.Should().BeTrue();
            budgets[0].Replenish(Window);
            budgets[0].OnDispose = () => throw failure;
            clock.Timer.Fire().Should().BeTrue("a handled retirement fault remains observed by the singleton owner");
            budgets[0].Disposals.Should().Be(1);
            budgets[1].Disposals.Should().Be(0);
            owner.AttemptAcquire(Identity("new-after-fault")).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryFault);
        }
        finally
        {
            // Exercise the actual DI lifetime boundary. Calling owner.Dispose
            // directly could hide a provider that failed to observe its fault.
            var observed = await Record.ExceptionAsync(async () =>
            {
                if (asynchronous) await provider.DisposeAsync().AsTask().WaitAsync(Timeout);
                else await Task.Run(provider.Dispose).WaitAsync(Timeout);
            });
            observed.Should().BeOfType<AggregateException>().Which.Flatten().InnerExceptions
                .Should().ContainSingle().Which.Should().BeSameAs(failure,
                    "teardown may observe only the exact planted failure, never hide an unrelated error");
        }

        var retainedAfterShutdown = () => owner.AttemptAcquire(Identity("retained"));
        var newAfterShutdown = () => owner.AttemptAcquire(Identity("new-after-shutdown"));
        retainedAfterShutdown.Should().Throw<ObjectDisposedException>();
        newAfterShutdown.Should().Throw<ObjectDisposedException>();
        budgets.Should().HaveCount(2).And.OnlyContain(budget => budget.Disposals == 1);
        budgets[0].CompletedDisposals.Should().Be(0, "the failed retired limiter was retained rather than retried");
        budgets[1].CompletedDisposals.Should().Be(1, "provider shutdown drains the remaining owned limiter");
        budgets.SelectMany(budget => budget.Leases).Should().OnlyContain(lease => lease.Disposals == 1);
        clock.Timer.AsyncDisposals.Should().Be(1);
    }

    [Fact]
    public async Task Acquisition_disposal_fault_refuses_without_allocation_or_debit_and_preserves_existing_quota()
    {
        using var clock = new ManualClock();
        using var metrics = new SubjectMetricCapture();
        List<TrackedBudget> budgets = [];
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(3);
            budgets.Add(budget);
            return budget;
        }, Window, clock, maximumPartitions: 2);
        var failure = new InvalidOperationException("synthetic subject cleanup failure");
        owner.AttemptAcquire(Identity("idle"));
        owner.AttemptAcquire(Identity("existing"));
        budgets[0].Replenish(Window);
        budgets[0].OnDispose = () => throw failure;
        try
        {
            var refused = owner.AttemptAcquire(Identity("new-key"));
            refused.Outcome.Should().Be(SubjectAdmissionOutcome.RegistryFault);
            budgets.Should().HaveCount(2);
            budgets.Sum(budget => budget.Attempts).Should().Be(2, "no permit is debited for the failed cleanup owner");
            budgets[0].Disposals.Should().Be(1);
            budgets[0].CompletedDisposals.Should().Be(0);

            owner.AttemptAcquire(Identity("other-new")).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryFault);
            owner.AttemptAcquire(Identity("existing")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            owner.AttemptAcquire(Identity("existing")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            owner.AttemptAcquire(Identity("existing")).Outcome.Should().Be(SubjectAdmissionOutcome.QuotaExhausted);
            budgets[1].Replenish(Window);
            var idleReads = budgets.Sum(budget => budget.IdleReads);
            clock.Timer.Fire();
            budgets.Sum(budget => budget.IdleReads).Should().Be(idleReads, "a sticky fault starts no later batch");
            budgets.Should().HaveCount(2);
            metrics.CleanupFailures.Should().ContainSingle().Which.Should().BeEquivalentTo(
                new SubjectMeasurement(TenantSubjectAdmissionMetrics.CleanupFailedCounterName, 1, []));
            metrics.Rejections.Should().BeEmpty("only middleware owns per-request rejection counting");
        }
        finally
        {
            var shutdownFailure = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask().WaitAsync(Timeout));
            shutdownFailure.Should().BeOfType<AggregateException>().Which.Flatten().InnerExceptions.Should().Contain(failure);
        }
        budgets.Should().OnlyContain(budget => budget.Disposals == 1);
    }

    [Fact]
    public async Task Cleanup_fault_owner_does_not_debit_a_same_key_installed_by_a_competing_request()
    {
        using var clock = new ManualClock();
        ConcurrentQueue<TrackedBudget> budgets = new();
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(2);
            budgets.Enqueue(budget);
            return budget;
        }, Window, clock, maximumPartitions: 1);
        owner.AttemptAcquire(Identity("idle"));
        var retiring = budgets.Single();
        retiring.Replenish(Window);
        var failure = new InvalidOperationException("synthetic same-key cleanup failure");
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        retiring.OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
            throw failure;
        };
        var acquisition = Task.Run(() => owner.AttemptAcquire(Identity("shared")));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            owner.AttemptAcquire(Identity("shared")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            budgets.Last().Attempts.Should().Be(1);
            release.Set();
            (await acquisition.WaitAsync(Timeout)).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryFault);
            budgets.Should().HaveCount(2);
            budgets.Last().Attempts.Should().Be(1,
                "the failed cleanup owner's decision takes precedence over a concurrently installed existing key");
            owner.AttemptAcquire(Identity("shared")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            budgets.Last().Attempts.Should().Be(2);
            owner.AttemptAcquire(Identity("unseen")).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryFault);
        }
        finally
        {
            release.Set();
            try { await acquisition.WaitAsync(Timeout); }
            finally
            {
                var shutdownFailure = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask().WaitAsync(Timeout));
                shutdownFailure.Should().BeOfType<AggregateException>().Which.Flatten().InnerExceptions.Should().Contain(failure);
            }
        }
        budgets.Should().OnlyContain(budget => budget.Disposals == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timer_disposal_fault_is_observed_once_published_before_batch_drain_and_joined_by_shutdown(bool asynchronous)
    {
        using var clock = new ManualClock();
        using var metrics = new SubjectMetricCapture();
        List<TrackedBudget> budgets = [];
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(3);
            budgets.Add(budget);
            return budget;
        }, Window, clock, maximumPartitions: 4);
        for (var index = 0; index < 4; index++) owner.AttemptAcquire(Identity($"subject-{index}"));
        budgets[0].Replenish(Window);
        budgets[1].Replenish(Window);
        var retirementFailure = new InvalidOperationException("synthetic first retirement failure");
        var shutdownFailure = new InvalidOperationException("synthetic retained shutdown failure");
        budgets[0].OnDispose = () => throw retirementFailure;
        budgets[2].OnDispose = () => throw shutdownFailure;
        var entered = Completion();
        using var release = new ManualResetEventSlim();
        budgets[1].OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var sweep = Task.Run(clock.Timer.Fire);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            // The second disposal has not returned, so observing fault here proves
            // publication follows the first failure rather than the whole batch.
            owner.AttemptAcquire(Identity("new-after-first-failure")).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryFault);
            owner.AttemptAcquire(Identity("subject-2")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            metrics.CleanupFailures.Should().ContainSingle();
            metrics.CleanupFailures[0].Value.Should().Be(1);
            metrics.CleanupFailures[0].Tags.Should().BeEmpty();
            var reads = budgets.Sum(budget => budget.IdleReads);
            await Task.Run(clock.Timer.Fire).WaitAsync(Timeout);
            budgets.Sum(budget => budget.IdleReads).Should().Be(reads);
            budgets.Should().HaveCount(4);
            release.Set();
            // A timer callback must finish normally despite a handled disposal fault.
            (await sweep.WaitAsync(Timeout)).Should().BeTrue();
            budgets[0].Disposals.Should().Be(1);
            budgets[1].Disposals.Should().Be(1);
            budgets[1].CompletedDisposals.Should().Be(1, "a prior failure cannot skip the rest of the batch");
            var idleReads = budgets.Sum(budget => budget.IdleReads);
            budgets[2].Replenish(Window);
            clock.Timer.Fire();
            budgets.Sum(budget => budget.IdleReads).Should().Be(idleReads, "fault remains sticky even when survivors become idle");
            owner.AttemptAcquire(Identity("another-new")).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryFault);
        }
        finally
        {
            release.Set();
            try { await sweep.WaitAsync(Timeout); }
            finally
            {
                var joinedFailure = await Record.ExceptionAsync(async () =>
                {
                    if (asynchronous) await owner.DisposeAsync().AsTask().WaitAsync(Timeout);
                    else await Task.Run(owner.Dispose).WaitAsync(Timeout);
                });
                joinedFailure.Should().BeOfType<AggregateException>().Which.Flatten().InnerExceptions
                    .Should().Contain(retirementFailure).And.Contain(shutdownFailure);
            }
        }
        budgets.Should().OnlyContain(budget => budget.Disposals == 1,
            "shutdown neither retries a failed retired resource nor skips retained resources after another failure");
        metrics.CleanupFailures.Should().ContainSingle();
        metrics.Rejections.Should().BeEmpty("timer transitions have no request rejection of their own");
    }
}

using System.Collections.Concurrent;
using FluentAssertions;
using LearnStack.Api.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed partial class TenantSubjectRateLimiterTests
{
    [Fact]
    public void Registered_production_default_admits_4096_subjects_and_refuses_4097_without_resetting_spent_quota()
    {
        using var provider = new ServiceCollection().AddLearnStackTenantSubjectAdmission().BuildServiceProvider();
        var owner = provider.GetRequiredService<TenantSubjectRateLimiter>();
        for (var index = 0; index < 4096; index++)
            owner.AttemptAcquire(Identity($"default-{index}")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
        for (var index = 1; index < 600; index++)
            owner.AttemptAcquire(Identity("default-0")).IsAcquired.Should().BeTrue();

        var overflow = owner.AttemptAcquire(Identity("default-4096"));
        overflow.Outcome.Should().Be(SubjectAdmissionOutcome.RegistryCapacity);
        overflow.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));
        owner.AttemptAcquire(Identity("default-0")).Outcome.Should().Be(SubjectAdmissionOutcome.QuotaExhausted);
        owner.AttemptAcquire(Identity("default-4095")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
    }

    [Fact]
    public void Default_bound_refusal_allocates_no_child_and_closes_all_4096_allocated_resources()
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Add(budget);
            return budget;
        }, Window, clock);
        try
        {
            for (var index = 0; index < 4096; index++)
                owner.AttemptAcquire(Identity($"subject-{index}")).IsAcquired.Should().BeTrue();
            for (var index = 0; index < 8; index++)
                owner.AttemptAcquire(Identity($"refused-{index}")).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryCapacity);
            budgets.Should().HaveCount(4096);
            budgets.Sum(budget => budget.Attempts).Should().Be(4096, "a refusal cannot debit another key");
            owner.AttemptAcquire(Identity("subject-0")).Outcome.Should().Be(SubjectAdmissionOutcome.QuotaExhausted);
            budgets[0].Attempts.Should().Be(2);
        }
        finally { owner.Dispose(); }

        budgets.Should().OnlyContain(budget => budget.Disposals == 1);
    }

    [Fact]
    public void Capacity_acquisition_inspects_at_most_128_and_rotates_live_survivors_then_retires_only_one()
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        using var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Add(budget);
            return budget;
        }, Window, clock, maximumPartitions: 130);
        for (var index = 0; index < 130; index++) owner.AttemptAcquire(Identity($"subject-{index}"));
        budgets[128].Replenish(Window);
        budgets[129].Replenish(Window);

        owner.AttemptAcquire(Identity("first-new")).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryCapacity);
        budgets.Sum(budget => budget.IdleReads).Should().Be(128);
        budgets.Sum(budget => budget.Disposals).Should().Be(0);
        budgets.Should().HaveCount(130);

        owner.AttemptAcquire(Identity("second-new")).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
        budgets.Sum(budget => budget.IdleReads).Should().Be(129, "the next acquisition advances beyond the all-live first batch");
        budgets[128].Disposals.Should().Be(1);
        budgets[129].Disposals.Should().Be(0, "the acquiring request retires only one even when the next entry is eligible");
        budgets.Should().HaveCount(131);
    }

    [Fact]
    public async Task Acquisition_cleanup_keeps_its_request_pending_while_existing_keys_and_free_slots_proceed()
    {
        using var clock = new ManualClock();
        ConcurrentQueue<TrackedBudget> budgets = new();
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(3);
            budgets.Enqueue(budget);
            return budget;
        }, Window, clock, maximumPartitions: 2);
        owner.AttemptAcquire(Identity("idle"));
        owner.AttemptAcquire(Identity("existing"));
        var retiring = budgets.First();
        retiring.Replenish(Window);
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        retiring.OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var acquisition = Task.Run(() => owner.AttemptAcquire(Identity("owner")));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            acquisition.IsCompleted.Should().BeFalse();
            budgets.Should().HaveCount(2, "the cleanup owner has not allocated or debited its own key");
            (await Task.Run(() => owner.AttemptAcquire(Identity("existing"))).WaitAsync(Timeout))
                .Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            (await Task.Run(() => owner.AttemptAcquire(Identity("competitor"))).WaitAsync(Timeout))
                .Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            (await Task.Run(() => owner.AttemptAcquire(Identity("overflow"))).WaitAsync(Timeout))
                .Outcome.Should().Be(SubjectAdmissionOutcome.RegistryCapacity);
            await Task.Run(clock.Timer.Fire).WaitAsync(Timeout);
            budgets.Sum(budget => budget.IdleReads).Should().Be(1, "a blocked acquisition owns the only retirement scan");
            budgets.Should().HaveCount(3);
            budgets.Last().Attempts.Should().Be(1);
        }
        finally
        {
            release.Set();
            try { await acquisition.WaitAsync(Timeout); }
            finally { await owner.DisposeAsync(); }
        }

        (await acquisition.WaitAsync(Timeout)).Outcome.Should().Be(SubjectAdmissionOutcome.RegistryCapacity,
            "a competing request may consume the freed slot; the cleanup owner has no reservation");
        budgets.Should().HaveCount(3).And.OnlyContain(budget => budget.Disposals == 1);
    }

    [Fact]
    public async Task Successful_acquisition_cleanup_reuses_a_concurrently_installed_same_key_instead_of_allocating_again()
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
        var retired = budgets.Single();
        retired.Replenish(Window);
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        retired.OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var acquisition = Task.Run(() => owner.AttemptAcquire(Identity("shared-new")));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            (await Task.Run(() => owner.AttemptAcquire(Identity("shared-new"))).WaitAsync(Timeout))
                .IsAcquired.Should().BeTrue();
            budgets.Should().HaveCount(2);
            budgets.Last().Attempts.Should().Be(1);
            release.Set();
            (await acquisition.WaitAsync(Timeout)).Outcome.Should().Be(SubjectAdmissionOutcome.Acquired);
            budgets.Should().HaveCount(2);
            budgets.Last().Attempts.Should().Be(2);
            owner.AttemptAcquire(Identity("shared-new")).Outcome.Should().Be(SubjectAdmissionOutcome.QuotaExhausted);
        }
        finally
        {
            release.Set();
            try { await acquisition.WaitAsync(Timeout); }
            finally { await owner.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task Shutdown_started_inside_acquisition_cleanup_is_rechecked_before_owner_allocation_or_debit()
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Add(budget);
            return budget;
        }, Window, clock, maximumPartitions: 1);
        owner.AttemptAcquire(Identity("idle"));
        budgets[0].Replenish(Window);
        Task? shutdown = null;
        budgets[0].OnDispose = () => shutdown = owner.DisposeAsync().AsTask();

        var acquire = Task.Run(() => owner.AttemptAcquire(Identity("after-cleanup")));
        try
        {
            var failure = await Record.ExceptionAsync(async () => await acquire.WaitAsync(Timeout));
            failure.Should().BeOfType<ObjectDisposedException>();
            shutdown.Should().NotBeNull();
            await shutdown!.WaitAsync(Timeout);
            budgets.Should().ContainSingle();
            budgets[0].Attempts.Should().Be(1);
            budgets[0].Disposals.Should().Be(1);
        }
        finally { await owner.DisposeAsync(); }
    }
}

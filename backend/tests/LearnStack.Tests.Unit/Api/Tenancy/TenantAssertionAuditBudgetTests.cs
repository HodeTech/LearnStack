using System.Collections;
using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

public sealed class TenantAssertionAuditBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_defended_tenants_cannot_allocate_or_spend_a_budget(bool sentinel)
    {
        var budget = new TenantAssertionAuditBudget(new AuditBudgetTime());
        var acquire = () => budget.TryAcquire(sentinel ? TenantId.PlatformSentinel.Value : Guid.Empty);

        acquire.Should().Throw<ArgumentException>().WithMessage("An independently defended tenant is required.*");

        State(budget).Windows.Count.Should().Be(0);
        State(budget).Queue.Should().BeEmpty();
        var tenant = Guid.NewGuid();
        for (var attempt = 0; attempt < 600; attempt++) budget.TryAcquire(tenant).Should().BeTrue();
        budget.TryAcquire(tenant).Should().BeFalse("rejecting an invalid key must not consume a real tenant's allowance");
    }

    [Fact]
    public void Exactly_600_concurrent_attempts_are_admitted_per_tenant()
    {
        var budget = new TenantAssertionAuditBudget(new AuditBudgetTime());
        var tenant = Guid.NewGuid();
        var admitted = 0;
        Parallel.For(0, 2400, _ =>
        {
            if (budget.TryAcquire(tenant)) Interlocked.Increment(ref admitted);
        });
        admitted.Should().Be(600);
        budget.TryAcquire(tenant).Should().BeFalse();
        budget.TryAcquire(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void Suppression_cannot_extend_first_request_expiry_and_wall_clock_changes_do_not_reset_it()
    {
        var time = new AuditBudgetTime();
        var budget = new TenantAssertionAuditBudget(time);
        var tenant = Guid.NewGuid();
        for (var attempt = 0; attempt < 600; attempt++) budget.TryAcquire(tenant).Should().BeTrue();
        time.Utc = DateTimeOffset.MaxValue;
        budget.TryAcquire(tenant).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(59));
        budget.TryAcquire(tenant).Should().BeFalse();
        time.Utc = DateTimeOffset.MinValue;
        time.Advance(TimeSpan.FromSeconds(1));
        budget.TryAcquire(tenant).Should().BeTrue();
        for (var attempt = 1; attempt < 600; attempt++) budget.TryAcquire(tenant).Should().BeTrue();
        budget.TryAcquire(tenant).Should().BeFalse();
    }

    [Fact]
    public void More_than_4096_live_tenants_are_independently_admitted_without_eviction()
    {
        var budget = new TenantAssertionAuditBudget(new AuditBudgetTime());
        var defended = Guid.NewGuid();
        for (var attempt = 0; attempt < 600; attempt++) budget.TryAcquire(defended).Should().BeTrue();
        for (var tenant = 0; tenant < 5000; tenant++) budget.TryAcquire(Guid.NewGuid()).Should().BeTrue();
        budget.TryAcquire(defended).Should().BeFalse("another tenant cannot reset a live consumed allowance");
        State(budget).Windows.Count.Should().Be(5001);
        AssertUniqueQueue(budget);
    }

    [Fact]
    public void An_expired_only_sweep_inspects_at_most_128_entries_per_call()
    {
        var time = new AuditBudgetTime();
        var budget = new TenantAssertionAuditBudget(time);
        for (var tenant = 0; tenant < 500; tenant++) budget.TryAcquire(Guid.NewGuid()).Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(1));
        budget.TryAcquire(Guid.NewGuid()).Should().BeTrue();
        State(budget).Windows.Count.Should().Be(500 - 128 + 1);
        AssertUniqueQueue(budget);
        for (var index = 0; index < 4; index++) budget.TryAcquire(Guid.NewGuid()).Should().BeTrue();
        State(budget).Windows.Count.Should().Be(5);
        AssertUniqueQueue(budget);
    }

    [Fact]
    public void Same_tenant_expiry_is_checked_even_when_the_bounded_sweep_has_not_reached_it()
    {
        var time = new AuditBudgetTime();
        var budget = new TenantAssertionAuditBudget(time);
        var defended = Guid.NewGuid();
        for (var attempt = 0; attempt < 600; attempt++) budget.TryAcquire(defended).Should().BeTrue();
        var other = Guid.NewGuid();
        for (var tenant = 0; tenant < 500; tenant++) budget.TryAcquire(Guid.NewGuid()).Should().BeTrue();
        for (var rotation = 0; State(budget).Queue.ToArray().ToList().IndexOf(defended) < 128; rotation++)
        {
            rotation.Should().BeLessThan(10, "the live queue must rotate with bounded work");
            budget.TryAcquire(other).Should().BeTrue();
        }
        time.Advance(TimeSpan.FromMinutes(1));
        budget.TryAcquire(defended).Should().BeTrue();
        AssertUniqueQueue(budget);
        for (var attempt = 1; attempt < 600; attempt++) budget.TryAcquire(defended).Should().BeTrue();
        budget.TryAcquire(defended).Should().BeFalse();
        AssertUniqueQueue(budget);
    }

    private static (IDictionary Windows, Queue<Guid> Queue) State(TenantAssertionAuditBudget budget) =>
        ((IDictionary)typeof(TenantAssertionAuditBudget).GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(budget)!,
            (Queue<Guid>)typeof(TenantAssertionAuditBudget).GetField("_sweep", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(budget)!);

    private static void AssertUniqueQueue(TenantAssertionAuditBudget budget)
    {
        var (windows, queue) = State(budget);
        queue.Should().OnlyHaveUniqueItems().And.HaveCount(windows.Count);
        queue.ToHashSet().SetEquals(windows.Keys.Cast<Guid>()).Should().BeTrue();
    }
}

internal sealed class AuditBudgetTime : TimeProvider
{
    private long _timestamp;
    public DateTimeOffset Utc { get; set; } = DateTimeOffset.UnixEpoch;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
    public override DateTimeOffset GetUtcNow() => Utc;
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
}

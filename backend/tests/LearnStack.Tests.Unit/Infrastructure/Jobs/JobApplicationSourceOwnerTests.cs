using System.Data.Common;
using FluentAssertions;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class JobApplicationSourceOwnerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Executor_join_waits_for_actual_cleanup_and_preserves_the_original_issuance_state(bool closed, bool dirty)
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var connectionCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = Substitute.For<DbConnection>();
#pragma warning disable CA2012 // This call configures NSubstitute; no real disposal ValueTask is issued.
        connection.DisposeAsync().Returns(new ValueTask(connectionCleanup.Task));
#pragma warning restore CA2012
        var resources = owner.BeginAuditConfigLoad();
        resources.HoldConnection(connection);
        var actual = FailedQueryAsync(resources);
        resources.HoldActualTask(actual);
        if (closed) owner.CloseIssuance();

        var join = owner.JoinAuxiliaryCleanupAsync();
        join.IsCompleted.Should().BeFalse();
        RefusesFurtherWork(owner);
        if (dirty) connectionCleanup.SetException(new InvalidOperationException("actual disposal fault"));
        else connectionCleanup.SetResult();

        (await join).Should().Be(!dirty);
        actual.IsFaulted.Should().BeTrue("a failed query is observed independently of its resource cleanup");
        owner.CleanupConfirmed.Should().Be(!dirty);
        var activate = () => owner.RequireFreshScopeReady();
        if (closed || dirty) activate.Should().Throw<InvalidOperationException>();
        else activate.Should().NotThrow("a successful framework admission keeps its original open owner");
        (await owner.JoinAuxiliaryCleanupAsync()).Should().Be(!dirty);
    }

    [Fact]
    public async Task Abandoned_loader_keeps_its_slot_until_actual_task_and_cleanup_both_finish()
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = Substitute.For<DbConnection>();
#pragma warning disable CA2012 // This call configures NSubstitute; no real disposal ValueTask is issued.
        connection.DisposeAsync().Returns(new ValueTask(connectionCleanup.Task));
#pragma warning restore CA2012
        var resources = owner.BeginAuditConfigLoad();
        resources.HoldConnection(connection);
        resources.HoldActualTask(actual.Task);

        var cleanup = resources.CleanupAsync().AsTask();
        cleanup.IsCompleted.Should().BeFalse();
        RefusesFurtherWork(owner);
        actual.SetResult();
        RefusesFurtherWork(owner);
        connectionCleanup.SetResult();
        await cleanup;

        owner.CleanupConfirmed.Should().BeTrue();
        owner.RequireFreshScopeReady();
    }

    [Fact]
    public async Task Positive_resource_cleanup_does_not_replace_the_actual_loader_join()
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resources = owner.BeginTenantFlagsLoad();
        resources.HoldActualTask(actual.Task);
        await resources.CleanupAsync();

        RefusesFurtherWork(owner);
        actual.SetResult();
        owner.CleanupConfirmed.Should().BeTrue();
        owner.RequireFreshScopeReady();
    }

    [Fact]
    public async Task Ordinary_sql_failure_with_positive_cleanup_allows_the_next_audit_intent()
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var actual = Task.FromException(new InvalidOperationException("ordinary SQL failure"));
        var resources = owner.BeginStandaloneAudit();
        resources.HoldActualTask(actual);
        await resources.CleanupAsync();
        await actual.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);

        owner.CleanupConfirmed.Should().BeTrue();
        var next = owner.BeginStandaloneAudit();
        next.HoldActualTask(Task.CompletedTask);
        await next.CleanupAsync();
        owner.RequireFreshScopeReady();
    }

    [Fact]
    public async Task A_disposal_fault_attempts_remaining_resources_and_permanently_seals_replacement()
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var connection = Substitute.For<DbConnection>();
        var transaction = Substitute.For<DbTransaction>();
        var command = Substitute.For<DbCommand>();
#pragma warning disable CA2012 // This call configures NSubstitute; no real disposal ValueTask is issued.
        command.DisposeAsync().Returns(ValueTask.FromException(new InvalidOperationException("cleanup fault")));
#pragma warning restore CA2012
        var resources = owner.BeginKillswitchLoad();
        resources.HoldConnection(connection);
        resources.HoldTransaction(transaction);
        resources.HoldCommand(command);
        resources.HoldActualTask(Task.CompletedTask);

        var cleanup = async () => await resources.CleanupAsync();
        await cleanup.Should().ThrowAsync<InvalidOperationException>().WithMessage("cleanup fault");
        await transaction.Received(1).DisposeAsync();
        await connection.Received(1).DisposeAsync();
        RefusesFurtherWork(owner);
        var second = async () => await resources.CleanupAsync();
        await second.Should().ThrowAsync<InvalidOperationException>();
        RefusesFurtherWork(owner);
    }

    [Fact]
    public async Task An_inherited_closed_binding_cannot_reenter_or_acquire_after_scope_restoration()
    {
        var owner = JobApplicationSourceOwner.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task escaped;
        using (owner.Enter())
        {
            escaped = Task.Run(async () =>
            {
                await release.Task;
                JobApplicationSourceOwner.Current.Should().BeSameAs(owner);
                var acquire = () => owner.BeginStandaloneAudit();
                acquire.Should().Throw<InvalidOperationException>();
                var reenter = () => owner.Enter();
                reenter.Should().Throw<InvalidOperationException>();
            });
        }
        JobApplicationSourceOwner.Current.Should().BeNull();
        using (owner.Enter()) owner.RequireFreshScopeReady();
        release.SetResult();
        await escaped;
    }

    [Fact]
    public void A_second_main_unit_refuses_and_faulted_first_cleanup_never_releases_its_slot()
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var first = Substitute.For<IUnitOfWork>();
        var second = Substitute.For<IUnitOfWork>();
        owner.BindMainUnit(first);
        var extra = () => owner.BindMainUnit(second);
        extra.Should().Throw<InvalidOperationException>();
        owner.RequireOwnedMainUnit(first);
        var fresh = () => owner.RequireFreshScopeReady();
        fresh.Should().Throw<InvalidOperationException>();
        owner.ReportMainCleanup(first, succeeded: false);
        owner.ReportMainCleanup(first, succeeded: true);
        owner.CleanupConfirmed.Should().BeFalse();
        extra.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Positive_auxiliary_cleanup_releases_its_slot_while_a_main_failure_keeps_issuance_sealed()
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var unit = Substitute.For<IUnitOfWork>();
        owner.BindMainUnit(unit);
        var resources = owner.BeginStandaloneAudit();
        resources.HoldActualTask(Task.CompletedTask);
        owner.ReportMainCleanup(unit, succeeded: false);

        await resources.CleanupAsync();

        owner.CleanupConfirmed.Should().BeFalse();
        RefusesFurtherWork(owner);
    }

    private static void RefusesFurtherWork(JobApplicationSourceOwner owner)
    {
        owner.CleanupConfirmed.Should().BeFalse();
        var audit = () => owner.BeginStandaloneAudit();
        audit.Should().Throw<InvalidOperationException>();
        var config = () => owner.BeginAuditConfigLoad();
        config.Should().Throw<InvalidOperationException>();
        var flags = () => owner.BeginTenantFlagsLoad();
        flags.Should().Throw<InvalidOperationException>();
        var killswitch = () => owner.BeginKillswitchLoad();
        killswitch.Should().Throw<InvalidOperationException>();
        var activate = () => owner.RequireBusinessReady();
        activate.Should().Throw<InvalidOperationException>();
    }

    private static async Task FailedQueryAsync(JobApplicationAuxiliaryResources resources)
    {
        try
        {
            await Task.Yield();
            throw new InvalidOperationException("ordinary query failure");
        }
        finally { await resources.CleanupAsync(); }
    }
}

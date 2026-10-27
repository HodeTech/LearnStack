using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class JobAuthorizationLaneTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_business_helper_reconciles_completed_confined_lane_before_its_queued_release_continuation(bool authorization)
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var lane = authorization ? owner.BeginAuthorizationRead() : owner.BeginAuthorizationEvidence();
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.HoldActualTask(actual.Task);
        await lane.CleanupAsync();
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var sync = typeof(JobApplicationSourceOwner).GetField("_sync", privateInstance)!.GetValue(owner)!;
        var retained = typeof(JobApplicationSourceOwner).GetField(authorization ? "_authorization" : "_evidence", privateInstance)!;
        JobApplicationAuxiliaryResources helper;
        lock (sync)
        {
            // The completed task's queued releaser cannot acquire this monitor. The helper
            // must reconcile the positively finished lane itself, on this reentrant path.
            actual.SetResult();
            retained.GetValue(owner).Should().BeSameAs(lane);
            helper = owner.BeginAuditConfigLoad();
            retained.GetValue(owner).Should().BeNull();
        }
        helper.HoldActualTask(Task.CompletedTask);
        await helper.CleanupAsync();
        owner.CleanupConfirmed.Should().BeTrue();
        owner.RequireFreshScopeReady();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Independent_lane_retains_actual_cleanup_and_joins_shutdown_with_the_main_unit_open(bool authorization, bool dirty)
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var main = Substitute.For<IUnitOfWork>();
        owner.BindMainUnit(main);
        var lane = authorization ? owner.BeginAuthorizationRead() : owner.BeginAuthorizationEvidence();
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = Substitute.For<DbConnection>();
#pragma warning disable CA2012 // Configures a substitute; no provider disposal is issued.
        connection.DisposeAsync().Returns(new ValueTask(cleanup.Task));
#pragma warning restore CA2012
        lane.HoldConnection(connection);
        lane.HoldActualTask(actual.Task);
        var dispose = lane.CleanupAsync().AsTask();
        var secondMain = () => owner.BindMainUnit(Substitute.For<IUnitOfWork>());
        var businessHelper = () => owner.BeginAuditConfigLoad();
        var secondAuthorization = () => owner.BeginAuthorizationRead();
        var secondEvidence = () => owner.BeginAuthorizationEvidence();
        secondMain.Should().Throw<InvalidOperationException>();
        businessHelper.Should().Throw<InvalidOperationException>();
        secondAuthorization.Should().Throw<InvalidOperationException>();
        secondEvidence.Should().Throw<InvalidOperationException>();
        owner.CloseIssuance();
        var join = owner.JoinAuxiliaryCleanupAsync();
        join.IsCompleted.Should().BeFalse();
        if (dirty) cleanup.SetException(new InvalidOperationException("actual lane cleanup fault"));
        else cleanup.SetResult();
        if (dirty) await FluentActions.Awaiting(() => dispose).Should().ThrowAsync<InvalidOperationException>();
        else await dispose;
        join.IsCompleted.Should().BeFalse("the actual operation is still retained after resource cleanup");
        actual.SetResult();
        (await join).Should().BeFalse("the original main unit is still owned");
        owner.ReportMainCleanup(main, succeeded: true);
        owner.CleanupConfirmed.Should().Be(!dirty);
    }

    [Fact]
    public async Task Reported_command_disposal_fault_cannot_be_replaced_by_successful_lane_connection_cleanup()
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var lane = owner.BeginAuthorizationRead();
        lane.HoldActualTask(Task.CompletedTask);
        lane.MarkCleanupFailed();
        await lane.CleanupAsync();
        (await owner.JoinAuxiliaryCleanupAsync()).Should().BeFalse();
        var resume = () => owner.RequireFreshScopeReady();
        resume.Should().Throw<InvalidOperationException>();
    }
}

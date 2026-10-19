using System.Reflection;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

/// <summary>Memory arbitration proofs; physical COMMIT and cleanup remain native database obligations.</summary>
public sealed class BackgroundJobRunGateTests
{
    [Fact]
    public void Acknowledged_stage_is_history_and_does_not_reopen_the_next_stopped_boundary()
    {
        var run = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop());
        var prepare = run.OpenBoundary(BackgroundJobCheckpointKind.Prepare);
        prepare.TryIssue().Should().BeTrue();
        BeginCommit(prepare).Should().BeTrue();
        var uncertainNext = () => run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        uncertainNext.Should().Throw<InvalidOperationException>("a COMMIT admission is not an acknowledgement");
        Observe(prepare, "RecordCommitted");
        run.IsClosed.Should().BeFalse();
        var arm = run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        run.Close();
        arm.TryIssue().Should().BeFalse();
        BeginCommit(arm).Should().BeFalse();
        prepare.CommitStarted.Should().BeTrue();
        prepare.CommitAcknowledged.Should().BeTrue();
        prepare.KnownRollback.Should().BeFalse();
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome).Should().BeNull();
        run.TryIssueExternalExecute(arm).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stop_and_commit_have_one_winner_and_late_acknowledgement_preserves_history(bool stopFirst)
    {
        var run = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop());
        var boundary = run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        boundary.TryIssue().Should().BeTrue();
        if (stopFirst) run.Close();
        BeginCommit(boundary).Should().Be(!stopFirst);
        run.Close();
        if (stopFirst) Observe(boundary, "RecordRolledBack");
        else Observe(boundary, "RecordCommitted");
        boundary.CommitStarted.Should().Be(!stopFirst);
        boundary.CommitAcknowledged.Should().Be(!stopFirst);
        boundary.KnownRollback.Should().Be(stopFirst);
        BeginCommit(boundary).Should().BeFalse();
        boundary.TryIssue().Should().BeFalse();
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Finalize).Should().BeNull();
    }

    [Fact]
    public void Process_stop_closes_prebound_admission_root_and_cleanup_without_a_cancellation_relay()
    {
        var runtime = new BackgroundJobRuntimeGate(BackgroundJobInfrastructureRegistry.CreateDefault());
        var admission = new BackgroundJobRunGate(runtime.RunStop).OpenBoundary(BackgroundJobCheckpointKind.Admission);
        var root = new BackgroundJobRunGate(runtime.RunStop).OpenBoundary(BackgroundJobCheckpointKind.Transactional);
        var cleanup = new BackgroundJobRunGate(runtime.RunStop).OpenBoundary(BackgroundJobCheckpointKind.TransactionalCleanup);
        admission.TryIssue().Should().BeTrue();
        cleanup.TryIssue().Should().BeTrue();
        runtime.StopStarts();
        runtime.IsOpen.Should().BeFalse();
        runtime.TryAdmit().Should().BeFalse();
        runtime.TryAdmitStorage().Should().BeTrue("stock shutdown still owns its removal and completion SQL");
        root.TryStartExecution().Should().BeFalse();
        BeginCommit(admission).Should().BeFalse();
        BeginCommit(cleanup).Should().BeFalse();
        admission.Run.IsClosed.Should().BeTrue();
        cleanup.Run.TryOpenBoundary(BackgroundJobCheckpointKind.TransactionalCleanup).Should().BeNull();
    }

    [Fact]
    public void Provider_entry_is_separate_from_prepare_entry_and_atomic_unused_sealing()
    {
        var run = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop());
        var prepare = run.OpenBoundary(BackgroundJobCheckpointKind.Prepare);
        prepare.TryStartExecution().Should().BeTrue();
        prepare.TryStartExecution().Should().BeFalse();
        run.TrySealUnusedExternalExecute().Should().BeTrue("the Prepare root did not call the provider");
        run.TryIssueExternalExecute(prepare).Should().BeFalse();
        prepare.TrySealUnusedExecution().Should().BeFalse("the actual root entry remains independently consumed");

        var issued = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop());
        var arm = issued.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        issued.TryIssueExternalExecute(arm).Should().BeFalse("a memory stage alone is not physical acknowledgement");
        arm.TryIssue().Should().BeTrue();
        BeginCommit(arm).Should().BeTrue();
        Observe(arm, "RecordCommitted"); // Synthetic ACK for this isolated memory arbiter only.
        issued.TryIssueExternalExecute(arm).Should().BeTrue();
        issued.TrySealUnusedExternalExecute().Should().BeFalse();
        issued.TryIssueExternalExecute(arm).Should().BeFalse();
    }

    [Fact]
    public void A_premature_refused_commit_cannot_seed_a_window_for_a_later_unbudgeted_ack()
    {
        var run = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop());
        var arm = run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        BeginCommit(arm).Should().BeFalse("the boundary has not been issued");
        arm.TryIssue().Should().BeTrue();
        ((bool)Method("TryBeginCommit").Invoke(arm, null)!).Should().BeTrue();
        Observe(arm, "RecordCommitted");
        arm.CommitAcknowledged.Should().BeTrue();
        arm.HasLiveAcknowledgedArm.Should().BeFalse("the earlier refused operation recorded no interval");
        run.TryIssueExternalExecute(arm).Should().BeFalse();
    }

    [Fact]
    public void A_synthetic_ack_without_an_owned_SQL_window_cannot_authorize_provider_entry()
    {
        var run = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop());
        var arm = run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        arm.TryIssue().Should().BeTrue();
        ((bool)Method("TryBeginCommit").Invoke(arm, null)!).Should().BeTrue();
        Observe(arm, "RecordCommitted");
        arm.CommitAcknowledged.Should().BeTrue();
        BeginCommit(arm).Should().BeFalse("a late budget cannot bind after an unbudgeted COMMIT/ACK");
        arm.HasLiveAcknowledgedArm.Should().BeFalse();
        run.TryIssueExternalExecute(arm).Should().BeFalse();
        run.TrySealUnusedExternalExecute().Should().BeTrue();
    }

    [Fact]
    public void Advancing_to_outcome_or_stopping_the_process_invalidates_even_an_acknowledged_unused_arm()
    {
        var process = new BackgroundJobRunGate.RuntimeStop();
        var run = new BackgroundJobRunGate(process);
        var arm = run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        arm.TryIssue().Should().BeTrue();
        BeginCommit(arm).Should().BeTrue();
        Observe(arm, "RecordCommitted");
        _ = run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        run.TryIssueExternalExecute(arm).Should().BeFalse("an old pre-outcome snapshot cannot authorize a new Execute");

        var another = new BackgroundJobRunGate(process);
        var current = another.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        current.TryIssue().Should().BeTrue();
        BeginCommit(current).Should().BeTrue();
        Observe(current, "RecordCommitted");
        process.Close();
        another.TryIssueExternalExecute(current).Should().BeFalse();
        current.CommitAcknowledged.Should().BeTrue("historical acknowledgement survives process stop");
    }

    private static bool BeginCommit(BackgroundJobCommitGate gate) => gate.Kind == BackgroundJobCheckpointKind.Arm
        // Synthetic interval for this memory-only arbiter, never a DB receipt.
        ? (bool)typeof(BackgroundJobCommitGate).GetMethod("TryBeginCheckpointCommit", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gate, [Stopwatch.GetTimestamp(), TimeSpan.FromMinutes(1)])!
        : (bool)Method("TryBeginCommit").Invoke(gate, null)!;

    private static void Observe(BackgroundJobCommitGate gate, string name) => Method(name).Invoke(gate, null);

    private static MethodInfo Method(string name) => typeof(BackgroundJobCommitGate).GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("The exact memory-only observation seam is missing.");
}

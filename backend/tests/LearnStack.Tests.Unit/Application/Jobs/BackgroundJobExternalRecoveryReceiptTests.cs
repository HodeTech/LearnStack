using System.Data;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

/// <summary>Projection/binding memory proofs only; SQL acquisition/ACK/cleanup require PostgreSQL.</summary>
public sealed class BackgroundJobExternalRecoveryReceiptTests
{
    [Fact]
    public void A_fresh_recovery_lease_preserves_expired_original_history_without_issuing_another_execute_or_counted_root()
    {
        var runtime = new BackgroundJobRunGate.RuntimeStop();
        var receipt = Fixture.Receipt(runtime);
        receipt.Deadline.Should().BeBefore(receipt.DbNow);
        receipt.OwnershipUntil.Should().BeAfter(receipt.DbNow);
        receipt.RecoveryToken.Should().NotBe(receipt.AdmissionToken);
        var run = new BackgroundJobRunGate(runtime);
        var source = JobApplicationSourceOwner.Create();
        BackgroundJobExternalRecoveryBinding recovery;
        using (source.Enter()) recovery = receipt.Bind(run);
        recovery.SourceOwner.Should().BeSameAs(source);
        recovery.Receipt.Should().BeSameAs(receipt);
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Admission).Should().BeNull();
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Prepare).Should().BeNull();
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Arm).Should().BeNull();
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Finalize).Should().BeNull();
        run.TryIssueExternalReconcile(recovery).Should().BeTrue();
        run.TryIssueExternalReconcile(recovery).Should().BeFalse();
        var outcome = run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        outcome.CommitAcknowledged.Should().BeFalse("recovery never fabricates a physical admission or stage acknowledgement");
        using (source.Enter())
        {
            Action reuse = () => receipt.Bind(new BackgroundJobRunGate(runtime));
            reuse.Should().Throw<InvalidOperationException>();
        }
    }

    [Theory]
    [InlineData("foreign_runtime")]
    [InlineData("stopped_run")]
    [InlineData("used_source")]
    [InlineData("old_boundary")]
    public void Recovery_refuses_a_foreign_stopped_used_or_preexisting_invocation(string defect)
    {
        var runtime = new BackgroundJobRunGate.RuntimeStop();
        var receipt = Fixture.Receipt(runtime);
        var run = new BackgroundJobRunGate(defect == "foreign_runtime" ? new() : runtime);
        var source = JobApplicationSourceOwner.Create();
        using var binding = source.Enter();
        if (defect == "stopped_run") run.Close();
        if (defect == "old_boundary") _ = run.OpenBoundary(BackgroundJobCheckpointKind.Admission);
        if (defect == "used_source")
        {
            var original = Substitute.For<IUnitOfWork>();
            source.BindMainUnit(original);
            source.ReportMainCleanup(original, succeeded: true);
            source.CleanupConfirmed.Should().BeTrue("disposal of an old source does not make it a new invocation");
        }
        Action attempt = () => receipt.Bind(run);
        attempt.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Process_stop_and_wrong_recovery_binding_close_reconcile_without_changing_immutable_history()
    {
        var runtime = new BackgroundJobRunGate.RuntimeStop();
        var first = Fixture.Receipt(runtime);
        var second = Fixture.Receipt(runtime);
        var run = new BackgroundJobRunGate(runtime);
        var another = new BackgroundJobRunGate(runtime);
        BackgroundJobExternalRecoveryBinding recovery, foreign;
        using (JobApplicationSourceOwner.Create().Enter()) recovery = first.Bind(run);
        using (JobApplicationSourceOwner.Create().Enter()) foreign = second.Bind(another);
        run.TryIssueExternalReconcile(foreign).Should().BeFalse();
        runtime.Close();
        run.TryIssueExternalReconcile(recovery).Should().BeFalse();
        first.AdmissionToken.Should().NotBeEmpty();
        first.NormalAttemptOrdinal.Should().Be(1);
        run.TryOpenBoundary(BackgroundJobCheckpointKind.NoStart).Should().BeNull();
    }

    [Theory]
    [InlineData(0, "non_v7")]
    [InlineData(1, "empty")]
    [InlineData(2, "empty")]
    [InlineData(4, "transactional")]
    [InlineData(9, "zero")]
    [InlineData(11, "backward")]
    [InlineData(12, "empty")]
    [InlineData(13, "zero")]
    [InlineData(14, "original_token")]
    [InlineData(15, "expired")]
    [InlineData(17, "finalize")]
    public void Recovery_projection_rejects_wrong_identity_stage_lease_or_original_ledger(int column, string defect)
    {
        Action capture = () => Fixture.Receipt(new(), column, defect);
        capture.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("permanent_failure", 1)]
    [InlineData("retry_exhausted", 6)]
    public void Terminal_recovery_opens_only_no_start_and_cannot_be_erased_into_an_ordinary_identity(string reason, int ordinal)
    {
        var runtime = new BackgroundJobRunGate.RuntimeStop();
        var receipt = Fixture.Receipt(runtime, terminalReason: reason, ordinal: ordinal);
        receipt.Purpose.Should().Be(BackgroundJobExternalRecoveryPurpose.TerminalNoStart);
        receipt.TerminalReason.Should().Be(reason);
        var run = new BackgroundJobRunGate(runtime);
        BackgroundJobExternalRecoveryBinding binding;
        using (JobApplicationSourceOwner.Create().Enter()) binding = receipt.Bind(run);
        run.TryIssueExternalReconcile(binding).Should().BeFalse();
        foreach (var kind in Enum.GetValues<BackgroundJobCheckpointKind>().Where(kind => kind != BackgroundJobCheckpointKind.NoStart))
            run.TryOpenBoundary(kind).Should().BeNull();
        Action ordinaryIdentity = () => BackgroundJobExecutionIdentity.FromRecovery(receipt);
        ordinaryIdentity.Should().Throw<InvalidOperationException>();
        var gate = run.OpenBoundary(BackgroundJobCheckpointKind.NoStart);
        Action ordinaryNoStart = () => RecordBackgroundJobOutcomeCommand.ForNoStart(binding, gate);
        ordinaryNoStart.Should().Throw<ArgumentException>();
        var command = RecordBackgroundJobOutcomeCommand.ForTerminalNoStart(binding, gate);
        command.IsTerminalNoStart.Should().BeTrue();
        command.IsNoStart.Should().BeTrue();
        gate.TryIssue().Should().BeTrue();
        run.Close();
        gate.IsClosed.Should().BeTrue();
    }

    [Theory]
    [InlineData(18, "terminal_pending")]
    [InlineData(18, "executing")]
    [InlineData(19, "permanent_failure")]
    public void Recovery_projection_refuses_a_mixed_or_unrecognized_purpose_pair(int column, string defect)
    {
        Action capture = () => Fixture.Receipt(new(), column, defect);
        capture.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_projection_refuses_absent_or_future_monotonic_source_observations(bool future)
    {
        Action capture = () => Fixture.Receipt(new(), observedAt: future ? long.MaxValue : 0);
        capture.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Binding_cannot_refresh_an_interval_already_spent_by_actual_acquisition_and_cleanup()
    {
        var runtime = new BackgroundJobRunGate.RuntimeStop();
        var receipt = Fixture.Receipt(runtime, observedAt: Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2,
            lease: TimeSpan.FromSeconds(1));
        var run = new BackgroundJobRunGate(runtime);
        using var source = JobApplicationSourceOwner.Create().Enter();
        Action bind = () => receipt.Bind(run);
        bind.Should().Throw<InvalidOperationException>();
        run.IsClosed.Should().BeTrue();
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Recovery_renewal_requires_every_original_and_current_identity_fact(int changedColumn)
    {
        var runtime = new BackgroundJobRunGate.RuntimeStop();
        var receipt = Fixture.Receipt(runtime);
        var run = new BackgroundJobRunGate(runtime);
        BackgroundJobExternalRecoveryBinding recovery;
        using (JobApplicationSourceOwner.Create().Enter()) recovery = receipt.Bind(run);
        var oldUntil = receipt.OwnershipUntil;
        var wrong = Fixture.Renewal(receipt, runtime, changedColumn);
        run.TryAcceptExternalRecoveryRenewal(recovery, wrong).Should().BeFalse();
        run.IsClosed.Should().BeFalse("unrelated metadata cannot refresh or close this still-live acquisition");
        var exact = Fixture.Renewal(receipt, runtime);
        run.TryAcceptExternalRecoveryRenewal(recovery, exact).Should().BeTrue("the matching positive control uses the same arbiter");
        receipt.OwnershipUntil.Should().Be(oldUntil, "renewal never rewrites historical acquisition facts");
        run.TryAcceptExternalRecoveryRenewal(recovery, exact).Should().BeFalse("replaying evidence cannot rebase its clock");
    }

    [Theory]
    [InlineData("process")]
    [InlineData("binding")]
    [InlineData("stopped")]
    [InlineData("stopped_process")]
    public void Renewal_cannot_move_between_processes_bindings_or_reopen_a_stopped_run(string defect)
    {
        var runtime = new BackgroundJobRunGate.RuntimeStop();
        var receipt = Fixture.Receipt(runtime);
        var run = new BackgroundJobRunGate(runtime);
        BackgroundJobExternalRecoveryBinding recovery;
        using (JobApplicationSourceOwner.Create().Enter()) recovery = receipt.Bind(run);
        var target = recovery;
        if (defect == "binding")
            using (JobApplicationSourceOwner.Create().Enter())
                target = Fixture.Clone(receipt, runtime).Bind(new BackgroundJobRunGate(runtime));
        var evidence = Fixture.Renewal(receipt, defect == "process" ? new() : runtime);
        if (defect == "stopped") run.Close();
        if (defect == "stopped_process") runtime.Close();
        run.TryAcceptExternalRecoveryRenewal(target, evidence).Should().BeFalse();
        if (defect is "stopped" or "stopped_process")
        {
            run.TryIssueExternalReconcile(recovery).Should().BeFalse();
            run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome).Should().BeNull();
        }
    }

    [Fact]
    public void An_older_renewal_observation_cannot_replace_a_newer_accepted_window()
    {
        var runtime = new BackgroundJobRunGate.RuntimeStop();
        var receipt = Fixture.Receipt(runtime);
        var run = new BackgroundJobRunGate(runtime);
        BackgroundJobExternalRecoveryBinding recovery;
        using (JobApplicationSourceOwner.Create().Enter()) recovery = receipt.Bind(run);
        var earlier = Fixture.Renewal(receipt, runtime, extension: TimeSpan.FromMinutes(2));
        var later = Fixture.Renewal(receipt, runtime);
        run.TryAcceptExternalRecoveryRenewal(recovery, later).Should().BeTrue();
        run.TryAcceptExternalRecoveryRenewal(recovery, earlier).Should().BeFalse();
        run.TryIssueExternalReconcile(recovery).Should().BeTrue();
        run.TryIssueExternalReconcile(recovery).Should().BeFalse("window renewal never remints provider entry");
    }

    [Fact]
    public async Task A_clean_new_probe_cannot_publish_from_an_original_acquisition_whose_actual_scope_cleanup_failed()
    {
        var registry = BackgroundJobDefinitionRegistry.CreateEmpty();
        var runtime = new BackgroundJobRuntimeGate(registry.Infrastructure);
        var process = runtime.RunStop;
        var candidate = Fixture.Receipt(process);
        var descriptor = Descriptor(candidate);
        var originalResources = new BackgroundJobControlResources
        {
            CommitAttempted = true,
            AuditScope = new AsyncServiceScope(new FailingScope()),
        };
        await originalResources.CleanupAsync();
        originalResources.CleanupConfirmed.Should().BeFalse();
        originalResources.AuditScope.Should().NotBeNull("the concrete failed owner stays retained");
        var original = new OwnedBackgroundJobExternalRecovery(new(null, originalResources, [descriptor]), process);
        var acquisitions = 0;
        await using var source = new PrivilegedDataSource(() =>
        {
            Interlocked.Increment(ref acquisitions);
            throw new InvalidOperationException("SQL must never be acquired from this origin.");
        });
        var store = new PostgresBackgroundJobControlStore(source, registry, new(), new SystemGuidFactory(),
            runtime);
        var later = await store.ProbeExternalRecoveryAsync(original, descriptor, CancellationToken.None);
        later.CleanupConfirmed.Should().BeTrue("only the new probe's empty resources are clean");
        later.Value.Should().BeNull();
        acquisitions.Should().Be(0);
        original.CleanupConfirmed.Should().BeFalse();
        original.Value.Should().BeNull();
        Action publish = () => original.PublishConfirmation(descriptor, candidate);
        publish.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Concurrent_confirmations_of_one_lost_ack_share_one_receipt_and_one_reconcile_binding()
    {
        var process = new BackgroundJobRunGate.RuntimeStop();
        var first = Fixture.Receipt(process);
        var second = Fixture.Clone(first, process);
        second.Should().NotBeSameAs(first);
        var descriptor = Descriptor(first);
        var resources = new BackgroundJobControlResources { CommitAttempted = true };
        await resources.CleanupAsync();
        var origin = new OwnedBackgroundJobExternalRecovery(new(null, resources, [descriptor]), process);
        var observed = await Task.WhenAll(Task.Run(() => origin.PublishConfirmation(descriptor, first)),
            Task.Run(() => origin.PublishConfirmation(descriptor, second)));
        observed[0].Should().BeSameAs(observed[1]);
        origin.Value.Should().BeSameAs(observed[0]);
        origin.CommitAcknowledged.Should().BeFalse("probe confirmation never changes a physical COMMIT acknowledgement");
        origin.CommitUncertain.Should().BeTrue();
        var run = new BackgroundJobRunGate(process);
        BackgroundJobExternalRecoveryBinding binding;
        using (JobApplicationSourceOwner.Create().Enter()) binding = observed[0].Bind(run);
        run.TryIssueExternalReconcile(binding).Should().BeTrue();
        using (JobApplicationSourceOwner.Create().Enter())
        {
            Action duplicate = () => observed[1].Bind(new BackgroundJobRunGate(process));
            duplicate.Should().Throw<InvalidOperationException>();
        }
        Action substituted = () => origin.PublishConfirmation(descriptor, Fixture.Receipt(process));
        substituted.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Lost_ack_origin_cannot_be_rebound_by_a_probe_using_another_process_cell()
    {
        var registry = BackgroundJobDefinitionRegistry.CreateEmpty();
        var originalRuntime = new BackgroundJobRuntimeGate(registry.Infrastructure);
        var otherRuntime = new BackgroundJobRuntimeGate(registry.Infrastructure);
        var candidate = Fixture.Receipt(originalRuntime.RunStop);
        var descriptor = Descriptor(candidate);
        var resources = new BackgroundJobControlResources { CommitAttempted = true };
        await resources.CleanupAsync();
        var origin = new OwnedBackgroundJobExternalRecovery(new(null, resources, [descriptor]), originalRuntime.RunStop);
        var acquisitions = 0;
        await using var source = new PrivilegedDataSource(() =>
        {
            Interlocked.Increment(ref acquisitions);
            throw new InvalidOperationException("Cross-process probing must refuse before SQL.");
        });
        var foreignStore = new PostgresBackgroundJobControlStore(source, registry, new(), new SystemGuidFactory(), otherRuntime);
        var probe = () => foreignStore.ProbeExternalRecoveryAsync(origin, descriptor, CancellationToken.None);
        await probe.Should().ThrowAsync<ArgumentException>();
        acquisitions.Should().Be(0);
        origin.Value.Should().BeNull();
    }

    private static BackgroundJobCommitProbe Descriptor(BackgroundJobExternalRecoveryReceipt receipt) =>
        new(BackgroundJobCommitBoundary.ExternalRecovery, receipt.JobId.Value, receipt.TenantId.Value,
            receipt.OrganizationId?.Value, receipt.DefinitionKey, receipt.Queue, receipt.WakeGeneration,
            receipt.AttemptId.Value, receipt.AdmissionToken, CapacityGroup: receipt.CapacityGroup,
            RecoveryToken: receipt.RecoveryToken, Ordinal: receipt.NormalAttemptOrdinal, Deadline: receipt.Deadline,
            AdmittedAt: receipt.AdmittedAt, OriginalDueAt: receipt.OriginalDueAt,
            ExpectedPhase: receipt.Purpose == BackgroundJobExternalRecoveryPurpose.TerminalNoStart
                ? "terminal_pending" : "reconciling", ExpectedTerminalReason: receipt.TerminalReason);

    private sealed class FailingScope : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => throw new InvalidOperationException("No service resolution in cleanup.");
        public void Dispose() => throw new InvalidOperationException("Actual async scope ownership is required.");
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("Synthetic actual scope disposal fault."));
    }

    private sealed class Fixture : BackgroundJobCapacityRecoveryReceipt.MintingSource
    {
        internal static BackgroundJobExternalRecoveryRenewal Renewal(BackgroundJobExternalRecoveryReceipt receipt,
            BackgroundJobRunGate.RuntimeStop runtime, int changedColumn = -1, TimeSpan? extension = null)
        {
            object[] cells = [receipt.JobId.Value, receipt.TenantId.Value, (object?)receipt.OrganizationId?.Value ?? DBNull.Value,
                receipt.DefinitionKey, "external", receipt.Queue, receipt.CapacityGroup, receipt.OriginalDueAt,
                receipt.AttemptId.Value, receipt.NormalAttemptOrdinal, receipt.AdmittedAt, receipt.Deadline,
                receipt.AdmissionToken, receipt.WakeGeneration, receipt.RecoveryToken, receipt.OwnershipUntil + (extension ?? TimeSpan.FromMinutes(1)),
                DateTimeOffset.UtcNow, "normal"];
            if (changedColumn >= 0) cells[changedColumn] = changedColumn switch
            {
                0 or 1 or 2 or 8 or 12 or 14 => Guid.CreateVersion7(),
                3 or 5 or 6 => "changed",
                7 or 10 or 11 => ((DateTimeOffset)cells[changedColumn]).AddTicks(10),
                9 => (int)cells[9] + 1,
                13 => (long)cells[13] + 1,
                _ => throw new InvalidOperationException("Unknown memory-proof substitution."),
            };
            using var table = new DataTable();
            for (var index = 0; index < cells.Length; index++)
                table.Columns.Add("c" + index, index == 2 ? typeof(Guid) : cells[index].GetType());
            table.Rows.Add(cells);
            using var row = table.CreateDataReader();
            row.Read().Should().BeTrue();
            return PublishExternalRecoveryRenewalCandidate(CaptureExternalRecoveryRenewalCandidate(row, runtime, Stopwatch.GetTimestamp()));
        }

        internal static BackgroundJobExternalRecoveryReceipt Clone(BackgroundJobExternalRecoveryReceipt receipt,
            BackgroundJobRunGate.RuntimeStop runtime) => Mint(runtime,
            [receipt.JobId.Value, receipt.TenantId.Value, (object?)receipt.OrganizationId?.Value ?? DBNull.Value,
                receipt.DefinitionKey, "external", receipt.Queue, receipt.CapacityGroup, receipt.OriginalDueAt,
                receipt.AttemptId.Value, receipt.NormalAttemptOrdinal, receipt.AdmittedAt, receipt.Deadline,
                receipt.AdmissionToken, receipt.WakeGeneration, receipt.RecoveryToken, receipt.OwnershipUntil,
                receipt.DbNow, "normal", receipt.Purpose == BackgroundJobExternalRecoveryPurpose.TerminalNoStart ? "terminal_pending" : "reconciling",
                (object?)receipt.TerminalReason ?? DBNull.Value]);

        internal static BackgroundJobExternalRecoveryReceipt Receipt(BackgroundJobRunGate.RuntimeStop runtime,
            int column = -1, string? defect = null, long? observedAt = null, TimeSpan? lease = null,
            string? terminalReason = null, int ordinal = 1)
        {
            var now = DateTimeOffset.UtcNow;
            object[] cells = [Guid.CreateVersion7(), Guid.CreateVersion7(), DBNull.Value, "fixture.external.v1", "external",
                "external", "fixture.resource", now.AddHours(-3), Guid.CreateVersion7(), ordinal,
                now.AddHours(-2), now.AddHours(-1), Guid.NewGuid(), 2L, Guid.NewGuid(), now + (lease ?? TimeSpan.FromMinutes(1)), now, "normal",
                terminalReason is null ? "reconciling" : "terminal_pending", (object?)terminalReason ?? DBNull.Value];
            if (column >= 0) cells[column] = defect switch
            {
                "non_v7" => Guid.NewGuid(),
                "empty" => Guid.Empty,
                "zero" => column == 13 ? (object)0L : 0,
                "backward" => now.AddHours(-3),
                "original_token" => cells[12],
                "expired" => now.AddSeconds(-1),
                _ => defect!,
            };
            return Mint(runtime, cells, observedAt);
        }

        private static BackgroundJobExternalRecoveryReceipt Mint(BackgroundJobRunGate.RuntimeStop runtime, object[] cells,
            long? observedAt = null)
        {
            using var table = new DataTable();
            for (var index = 0; index < cells.Length; index++)
                table.Columns.Add("c" + index, index == 2 ? typeof(Guid) : index == 19 ? typeof(string) : cells[index].GetType());
            table.Rows.Add(cells);
            using var row = table.CreateDataReader();
            row.Read().Should().BeTrue();
            return PublishExternalRecoveryCandidate(CaptureExternalRecoveryCandidate(row, runtime, observedAt ?? Stopwatch.GetTimestamp()));
        }
    }
}

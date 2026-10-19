using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Unit.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed partial class BackgroundJobExecutionControlTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Joining_rejects_late_renewal_after_actual_timer_disposal_without_disturbing_a_live_batch_member()
    {
        await using var source = new PrivilegedDataSource(() =>
            throw new InvalidOperationException("This memory-only fixture must not resolve a database source."));
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var time = new DisposalPauseTimeProvider();
        using var observation = new BackgroundJobObservation(definitions, time);
        var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
            new BackgroundJobExecutionAdmission(definitions, definitions, new AuditCatalog([new BackgroundJobAuditCatalogSource()])), new(), observation);
        var owner = new BackgroundJobControl(source, definitions, executor,
            gate, new(), new SystemGuidFactory(), time);
        var generation = new HangfireGenerationWork(new("probe", BackgroundJobWorkerPoolKind.Transactional, 2, ["probe"]), gate);
        var failed = await BeginAsync(owner, generation);
        var healthy = await BeginAsync(owner, generation);
        var failedToken = failed.Execution.Token;
        var healthyToken = healthy.Execution.Token;
        var failedBusiness = new TaskCompletionSource<JobBusinessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyBusiness = new TaskCompletionSource<JobBusinessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        failed.Execution.HoldBusiness(failedBusiness.Task);
        failed.Invocation.HoldBusiness(failedBusiness.Task);
        healthy.Execution.HoldBusiness(healthyBusiness.Task);
        healthy.Invocation.HoldBusiness(healthyBusiness.Task);
        var failedTimer = time.LeaseTimer(failed.Execution);
        var healthyTimer = time.LeaseTimer(healthy.Execution);
        failedTimer.HoldDisposal = true;

        // These exact acknowledged identities were selected before the failed
        // attempt finished. Its successful renewal returns during timer cleanup.
        var issuedAt = time.GetTimestamp();
        var failedRenewal = Renewal(failed.Execution);
        var healthyRenewal = Renewal(healthy.Execution);
        failedBusiness.SetResult(KnownRollback(failed.Receipt, failed.Execution.Gate));
        failed.Execution.Gate.Close();
        Task<bool>? joining = null;
        try
        {
            failed.Execution.RenewalPermitted.Should().BeFalse("the sticky run closure also closes renewal");
            healthy.Execution.RenewalPermitted.Should().BeTrue();
            failed.Execution.Gate.CommitAcknowledged.Should().BeFalse();
            joining = failed.Execution.JoinAsync(operationCleanupConfirmed: true);
            await failedTimer.Disposed.WaitAsync(TestWait);

            // DisposeAsync has disposed the real System timer but cannot yet
            // return. Publishing joined after disposal cannot protect this gap.
            failed.Execution.RecordRenewal(failedRenewal, issuedAt).Should().BeFalse();
            healthy.Execution.RecordRenewal(healthyRenewal, issuedAt).Should().BeTrue();
            failedTimer.ChangesAfterDisposal.Should().Be(0);
            failedTimer.Changes.Should().Be(0);
            healthyTimer.Changes.Should().Be(1);
            failed.Execution.RenewalPermitted.Should().BeFalse();
            healthy.Execution.RenewalPermitted.Should().BeTrue();
            failed.Execution.Gate.IsClosed.Should().BeTrue();
            failed.Execution.Gate.CommitAcknowledged.Should().BeFalse();
            failedToken.IsCancellationRequested.Should().BeFalse("joining completed work needs no cancellation relay");
            healthy.Execution.Gate.IsClosed.Should().BeFalse();
            healthyToken.IsCancellationRequested.Should().BeFalse();
            healthyBusiness.Task.IsCompleted.Should().BeFalse();
            joining.IsCompleted.Should().BeFalse();
            owner.Snapshot.Executions.Should().Be(2, "timer disposal has not supplied actual cleanup");
            var releaseEarly = () => failed.Execution.ReleaseAfterPrivateCleanup();
            releaseEarly.Should().Throw<InvalidOperationException>();

            failedTimer.ReleaseDisposal();
            (await joining.WaitAsync(TestWait)).Should().BeTrue();
            failed.Execution.CleanupFault.Should().BeFalse();
            owner.Snapshot.Executions.Should().Be(2, "the generation still owns private cleanup after the timer joins");
            healthy.Execution.Gate.IsClosed.Should().BeFalse();
            healthyToken.IsCancellationRequested.Should().BeFalse();
        }
        finally
        {
            failedTimer.ReleaseDisposal();
            healthyBusiness.TrySetResult(KnownRollback(healthy.Receipt, healthy.Execution.Gate));
            joining ??= failed.Execution.JoinAsync(operationCleanupConfirmed: true);
            await failed.Invocation.CompleteAsync(await joining.WaitAsync(TestWait)).WaitAsync(TestWait);
            await healthy.Invocation.CompleteAsync(await healthy.Execution.JoinAsync(operationCleanupConfirmed: true)
                .WaitAsync(TestWait)).WaitAsync(TestWait);
            generation.CloseStarts();
            owner.RequestStop();
            await Task.WhenAll(generation.Completion, owner.Completion).WaitAsync(TestWait);
        }

        owner.Snapshot.Executions.Should().Be(0);
        failedTimer.Disposals.Should().Be(1);
        healthyTimer.Disposals.Should().Be(1);
        var failedHandle = () => failedToken.WaitHandle;
        var healthyHandle = () => healthyToken.WaitHandle;
        failedHandle.Should().Throw<ObjectDisposedException>();
        healthyHandle.Should().Throw<ObjectDisposedException>();
        owner.Snapshot.LeaseLossElapsed.Should().Be(0);
        owner.Snapshot.LeaseLossRenewalRefused.Should().Be(0);
    }

    [Fact]
    public async Task Joining_waits_for_the_actual_renewal_timer_update_without_holding_fixed_state_or_inventing_cleanup_failure()
    {
        await using var source = new PrivilegedDataSource(() =>
            throw new InvalidOperationException("This memory-only fixture must not resolve a database source."));
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var time = new DisposalPauseTimeProvider();
        using var observation = new BackgroundJobObservation(definitions, time);
        var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
            new BackgroundJobExecutionAdmission(definitions, definitions, new AuditCatalog([new BackgroundJobAuditCatalogSource()])), new(), observation);
        var owner = new BackgroundJobControl(source, definitions, executor, gate, new(), new SystemGuidFactory(), time);
        var generation = new HangfireGenerationWork(new("probe", BackgroundJobWorkerPoolKind.Transactional, 1, ["probe"]), gate);
        var current = await BeginAsync(owner, generation);
        var timer = time.LeaseTimer(current.Execution);
        timer.HoldChange = true;
        var business = Task.FromResult(KnownRollback(current.Receipt, current.Execution.Gate));
        current.Execution.HoldBusiness(business);
        current.Invocation.HoldBusiness(business);
        var issuedAt = time.GetTimestamp();
        Task<bool>? updating = null;
        Task<bool>? joining = null;
        Task<bool>? fixedState = null;
        try
        {
            updating = Task.Run(() => current.Execution.RecordRenewal(Renewal(current.Execution), issuedAt));
            await timer.ChangeEntered.WaitAsync(TestWait);
            current.Execution.RunGate.Close();
            fixedState = Task.Run(() => current.Execution.CleanupFault);
            (await fixedState.WaitAsync(TestWait)).Should().BeFalse("the actual Change prefix holds no fixed-state lock");
            joining = current.Execution.JoinAsync(operationCleanupConfirmed: true);
            joining.IsCompleted.Should().BeFalse("the update has not returned its real cleanup witness");
            timer.Disposals.Should().Be(0);
            owner.Snapshot.Executions.Should().Be(1);
            timer.ReleaseChange();
            (await updating.WaitAsync(TestWait)).Should().BeFalse("stop won while the successful timer update was in flight");
            (await joining.WaitAsync(TestWait)).Should().BeTrue("a declined renewal is distinct from failed cleanup");
            current.Execution.CleanupFault.Should().BeFalse();
            timer.Disposals.Should().Be(1);
            timer.ChangesAfterDisposal.Should().Be(0);
            owner.Snapshot.Executions.Should().Be(1, "private native cleanup still owns the identity");
        }
        finally
        {
            timer.ReleaseChange();
            if (updating is not null) await updating;
            if (fixedState is not null) await fixedState;
            joining ??= current.Execution.JoinAsync(operationCleanupConfirmed: true);
            await current.Invocation.CompleteAsync(await joining.WaitAsync(TestWait));
            generation.CloseStarts();
            owner.RequestStop();
            await Task.WhenAll(generation.Completion, owner.Completion).WaitAsync(TestWait);
        }
        owner.Snapshot.Executions.Should().Be(0);
        owner.Snapshot.LeaseLossElapsed.Should().Be(0);
        owner.Snapshot.LeaseLossRenewalRefused.Should().Be(0);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("elapsed")]
    [InlineData("stopped")]
    [InlineData("joined")]
    [InlineData("settled")]
    [InlineData("unpublished")]
    [InlineData("recording_without_closure")]
    public async Task First_lease_loss_requires_published_provenance_and_keeps_its_first_cause_after_cleanup(string mode)
    {
        await using var source = new PrivilegedDataSource(() => throw new InvalidOperationException("The lease observation fixture opens no database."));
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var time = new DisposalPauseTimeProvider();
        using var observation = new BackgroundJobObservation(definitions, time);
        var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
            new(definitions, definitions, new AuditCatalog([new BackgroundJobAuditCatalogSource()])), new(), observation);
        var owner = new BackgroundJobControl(source, definitions, executor, gate, new(), new SystemGuidFactory(), time);
        var generation = new HangfireGenerationWork(new("probe", BackgroundJobWorkerPoolKind.Transactional, 1, ["probe"]), gate);
        var current = await BeginAsync(owner, generation, publish: mode != "unpublished");
        try
        {
            if (mode == "stopped") gate.StopStarts();
            if (mode == "joined") (await current.Execution.JoinBusinessObserversAsync()).Should().BeTrue();
            if (mode == "settled")
            {
                current.Execution.Gate.TryIssue().Should().BeTrue();
                ((bool)typeof(BackgroundJobCommitGate).GetMethod("TryBeginCommit", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(current.Execution.Gate, null)!).Should().BeTrue();
                // Memory-only acknowledged gate history; no physical COMMIT claim.
                typeof(BackgroundJobCommitGate).GetMethod("RecordCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(current.Execution.Gate, null);
            }
            if (mode == "refused") current.Execution.RefuseRenewal();
            if (mode == "recording_without_closure")
            {
                var record = typeof(BackgroundJobExecutionControl).GetMethod("ObserveLeaseLoss", BindingFlags.Instance | BindingFlags.NonPublic)!;
                record.Invoke(current.Execution, [BackgroundJobLeaseLossCause.None]);
                record.Invoke(current.Execution, [(BackgroundJobLeaseLossCause)99]);
                owner.Snapshot.LeaseLossRenewalRefused.Should().Be(0);
                for (var attempt = 0; attempt < 2; attempt++) record.Invoke(current.Execution, [BackgroundJobLeaseLossCause.RenewalRefused]);
                current.Execution.RunGate.IsClosed.Should().BeFalse("recording is not execution authority closure");
                current.Execution.RefuseRenewal();
            }
            time.Advance(TimeSpan.FromSeconds(61));
            if (mode != "unpublished") time.LeaseTimer(current.Execution).FireCallback();
            current.Execution.RefuseRenewal();
            current.Execution.RefuseRenewal();
            owner.Snapshot.LeaseLossElapsed.Should().Be(mode == "elapsed" ? 1 : 0);
            owner.Snapshot.LeaseLossRenewalRefused.Should().Be(mode is "refused" or "recording_without_closure" ? 1 : 0);
            source.IsValueCreated.Should().BeFalse();
        }
        finally
        {
            await current.Invocation.CompleteAsync(await current.Execution.JoinAsync(operationCleanupConfirmed: true).WaitAsync(TestWait));
            generation.CloseStarts();
            owner.RequestStop();
            await Task.WhenAll(generation.Completion, owner.Completion).WaitAsync(TestWait);
        }
        owner.Snapshot.Executions.Should().Be(0);
        owner.Snapshot.LeaseLossElapsed.Should().Be(mode == "elapsed" ? 1 : 0);
        owner.Snapshot.LeaseLossRenewalRefused.Should().Be(mode is "refused" or "recording_without_closure" ? 1 : 0);
    }

    private static async Task<(BackgroundJobExecutionControl Execution, HangfireGenerationWork.Invocation Invocation,
        ConfirmedStartReceipt Receipt)> BeginAsync(BackgroundJobControl owner, HangfireGenerationWork generation, bool publish = true)
    {
        var receipt = JobAdmissionFixtureReceipt.Create("tenancy.probe.v1", TenantId.From(Guid.CreateVersion7()),
            null, BackgroundJobExecutionStage.Transactional);
        var invocation = generation.TryBegin() ?? throw new InvalidOperationException("The test invocation was not admitted.");
        var execution = owner.TryBegin(receipt.Claim, invocation)
            ?? throw new InvalidOperationException("The test execution was not admitted.");
        invocation.HoldExecution(execution);
        receipt = JobAdmissionFixtureReceipt.Create(receipt.Envelope.DefinitionKey, receipt.Envelope.TenantId,
            receipt.Envelope.OrganizationId, receipt.Stage, execution.AdmissionGate, receipt.Claim, bindRun: false, acknowledgeAdmission: publish);
        var admission = Task.FromResult(new JobStartExecutionResult(publish ? JobStartExecutionOutcome.Started : JobStartExecutionOutcome.LostClaim,
            publish ? receipt : null, false, receipt.SourceOwner));
        invocation.HoldAdmission(admission);
        execution.HoldAdmission(admission);
        // Reuse the isolated receipt fixture; production publication authority
        // remains confined to the actual acknowledged admission coordinator.
        var publication = (TaskCompletionSource<ConfirmedStartReceipt?>)(typeof(BackgroundJobStartPublication)
            .GetField("_completion", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(execution.Publication)
            ?? throw new InvalidOperationException("The admission publication is missing."));
        publication.SetResult(publish ? receipt : null);
        await execution.WaitForPublicationAsync().WaitAsync(TestWait);
        return (execution, invocation, receipt);
    }

    private static JobBusinessExecutionResult KnownRollback(ConfirmedStartReceipt receipt, BackgroundJobCommitGate gate) =>
        new(new(BackgroundJobFailureDisposition.Retry, "dependency_unavailable", null), false, false, true, false, false,
            receipt, gate, executionIssued: true);

    private static BackgroundJobRenewal Renewal(BackgroundJobExecutionControl execution)
    {
        var identity = execution.Identity ?? throw new InvalidOperationException("The acknowledged identity is missing.");
        return new(identity, identity.AdmittedAt.AddSeconds(90), identity.AdmittedAt);
    }

    private sealed class DisposalPauseTimeProvider : TimeProvider
    {
        private readonly Dictionary<BackgroundJobExecutionControl, DisposalPauseTimer> _leaseTimers = [];
        private long _offset;
        public override long GetTimestamp() => System.GetTimestamp() + Interlocked.Read(ref _offset);
        internal void Advance(TimeSpan value) => Interlocked.Add(ref _offset, (long)(value.TotalSeconds * TimestampFrequency));

        internal DisposalPauseTimer LeaseTimer(BackgroundJobExecutionControl execution)
        {
            lock (_leaseTimers) return _leaseTimers[execution];
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = System.CreateTimer(callback, state, dueTime, period);
            if (state is not BackgroundJobExecutionControl execution) return timer;
            var owned = new DisposalPauseTimer(timer, callback, state);
            lock (_leaseTimers) _leaseTimers.Add(execution, owned);
            return owned;
        }
    }

    private sealed class DisposalPauseTimer(ITimer inner, TimerCallback callback, object? state) : ITimer
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _changes, _changesAfterDisposal, _disposals;

        private readonly TaskCompletionSource _changeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _releaseChange = new();
        internal bool HoldChange { get; set; }
        internal Task ChangeEntered => _changeEntered.Task;
        internal void ReleaseChange() => _releaseChange.Set();
        internal bool HoldDisposal { get; set; }
        internal Task Disposed => _disposed.Task;
        internal int Changes => Volatile.Read(ref _changes);
        internal int ChangesAfterDisposal => Volatile.Read(ref _changesAfterDisposal);
        internal int Disposals => Volatile.Read(ref _disposals);
        internal void ReleaseDisposal() => _release.TrySetResult();
        internal void FireCallback() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (HoldChange)
            {
                _changeEntered.TrySetResult();
                if (!_releaseChange.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The held timer Change was not released.");
            }
            Interlocked.Increment(ref _changes);
            if (_disposed.Task.IsCompleted) Interlocked.Increment(ref _changesAfterDisposal);
            return inner.Change(dueTime, period);
        }

        public void Dispose()
        {
            inner.Dispose();
            _releaseChange.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            await inner.DisposeAsync();
            _releaseChange.Dispose();
            _disposed.TrySetResult();
            if (HoldDisposal) await _release.Task;
        }
    }
}

using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.Tests.Unit.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobExecutionControlTests
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
        var owner = new BackgroundJobControl(source, definitions, Substitute.For<IServiceScopeFactory>(),
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
        failedBusiness.SetResult(KnownRollback(failed.Receipt));
        failed.Execution.Gate.Close();
        Task<bool>? joining = null;
        try
        {
            failed.Execution.RenewalPermitted.Should().BeTrue();
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
            healthyBusiness.TrySetResult(KnownRollback(healthy.Receipt));
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
    }

    private static async Task<(BackgroundJobExecutionControl Execution, HangfireGenerationWork.Invocation Invocation,
        ConfirmedStartReceipt Receipt)> BeginAsync(BackgroundJobControl owner, HangfireGenerationWork generation)
    {
        var receipt = JobAdmissionFixtureReceipt.Create("tenancy.probe.v1", TenantId.From(Guid.CreateVersion7()),
            null, BackgroundJobExecutionStage.Transactional);
        var invocation = generation.TryBegin() ?? throw new InvalidOperationException("The test invocation was not admitted.");
        var execution = owner.TryBegin(receipt.Claim, invocation)
            ?? throw new InvalidOperationException("The test execution was not admitted.");
        invocation.HoldExecution(execution);
        var admission = Task.FromResult(new JobStartExecutionResult(JobStartExecutionOutcome.Started, receipt, false, receipt.SourceOwner));
        invocation.HoldAdmission(admission);
        execution.HoldAdmission(admission);
        // Reuse the isolated receipt fixture; production publication authority
        // remains confined to the actual acknowledged admission coordinator.
        var publication = (TaskCompletionSource<ConfirmedStartReceipt?>)(typeof(BackgroundJobStartPublication)
            .GetField("_completion", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(execution.Publication)
            ?? throw new InvalidOperationException("The admission publication is missing."));
        publication.SetResult(receipt);
        await execution.WaitForPublicationAsync().WaitAsync(TestWait);
        return (execution, invocation, receipt);
    }

    private static JobBusinessExecutionResult KnownRollback(ConfirmedStartReceipt receipt) =>
        new(new(BackgroundJobFailureDisposition.Retry, "dependency_unavailable", null), false, false, true, false, false)
        { SourceOwner = receipt.SourceOwner };

    private static BackgroundJobRenewal Renewal(BackgroundJobExecutionControl execution)
    {
        var identity = execution.Identity ?? throw new InvalidOperationException("The acknowledged identity is missing.");
        return new(identity, identity.AdmittedAt.AddSeconds(90), identity.AdmittedAt);
    }

    private sealed class DisposalPauseTimeProvider : TimeProvider
    {
        private readonly Dictionary<BackgroundJobExecutionControl, DisposalPauseTimer> _leaseTimers = [];

        internal DisposalPauseTimer LeaseTimer(BackgroundJobExecutionControl execution)
        {
            lock (_leaseTimers) return _leaseTimers[execution];
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = System.CreateTimer(callback, state, dueTime, period);
            if (state is not BackgroundJobExecutionControl execution) return timer;
            var owned = new DisposalPauseTimer(timer);
            lock (_leaseTimers) _leaseTimers.Add(execution, owned);
            return owned;
        }
    }

    private sealed class DisposalPauseTimer(ITimer inner) : ITimer
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _changes, _changesAfterDisposal, _disposals;

        internal bool HoldDisposal { get; set; }
        internal Task Disposed => _disposed.Task;
        internal int Changes => Volatile.Read(ref _changes);
        internal int ChangesAfterDisposal => Volatile.Read(ref _changesAfterDisposal);
        internal int Disposals => Volatile.Read(ref _disposals);
        internal void ReleaseDisposal() => _release.TrySetResult();

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _changes);
            if (_disposed.Task.IsCompleted) Interlocked.Increment(ref _changesAfterDisposal);
            return inner.Change(dueTime, period);
        }

        public void Dispose() => inner.Dispose();

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            await inner.DisposeAsync();
            _disposed.TrySetResult();
            if (HoldDisposal) await _release.Task;
        }
    }
}

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

public sealed class BackgroundJobCapacityCleanupTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Never_started_requires_atomic_unused_entry_revocation_even_when_the_commit_gate_is_closed()
    {
        var merelyClosed = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop())
            .OpenBoundary(BackgroundJobCheckpointKind.Transactional);
        merelyClosed.TryStartExecution().Should().BeTrue();
        merelyClosed.Close();
        merelyClosed.TrySealUnusedExecution().Should().BeFalse();

        var unused = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop())
            .OpenBoundary(BackgroundJobCheckpointKind.Transactional);
        unused.TrySealUnusedExecution().Should().BeTrue();
        unused.IsClosed.Should().BeTrue();
        unused.TryStartExecution().Should().BeFalse();
        unused.TrySealUnusedExecution().Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Cleanup_witness_waits_for_actual_private_callbacks_and_keeps_only_the_original_owner_open(
        bool businessRan, bool lostAdmissionAcknowledgement)
    {
        await using var fixture = new Fixture();
        var (execution, invocation, receipt) = await fixture.BeginAsync(lostAdmissionAcknowledgement);
        if (businessRan)
        {
            execution.Gate.TryStartExecution().Should().BeTrue();
            var business = Task.FromResult(new JobBusinessExecutionResult(
                new(BackgroundJobFailureDisposition.Retry, "dependency_unavailable", null), false, false, true, false, false,
                receipt, execution.Gate, executionIssued: true));
            execution.HoldBusiness(business);
            invocation.HoldBusiness(business);
        }
        execution.Gate.Close();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        CancellationTokenRegistration callback;
        using (receipt.SourceOwner.Enter())
        {
            callback = invocation.Token.Register(() =>
            {
                entered.TrySetResult();
                release.Wait(TestWait).Should().BeTrue();
                JobApplicationSourceOwner.Current.Should().BeSameAs(receipt.SourceOwner);
                var acquire = () => receipt.SourceOwner.BeginStandaloneAudit();
                acquire.Should().Throw<InvalidOperationException>("the original business binding stays closed");
                var reenter = () => receipt.SourceOwner.Enter();
                reenter.Should().Throw<InvalidOperationException>();
            });
        }

        Task<BackgroundJobActualCleanup?>? preparation = null;
        try
        {
            preparation = invocation.PrepareCapacityCleanupAsync(execution);
            await entered.Task.WaitAsync(TestWait);
            var beforeCallbacksJoin = await Task.WhenAny(preparation, Task.Delay(TimeSpan.FromMilliseconds(100)));
            beforeCallbacksJoin.Should().NotBeSameAs(preparation, "a held private callback prevents cleanup publication");
            preparation.IsCompleted.Should().BeFalse();
            fixture.Control.Snapshot.Executions.Should().Be(1);
            execution.RenewalPermitted.Should().BeFalse();
            execution.Gate.TryStartExecution().Should().BeFalse();
            var releaseEarly = () => execution.ReleaseAfterPrivateCleanup();
            releaseEarly.Should().Throw<InvalidOperationException>();

            release.Set();
            var cleanup = await preparation.WaitAsync(TestWait);
            cleanup.Should().NotBeNull();
            cleanup!.Evidence.Should().Be(businessRan ? BackgroundJobCapacityCleanupEvidence.TransactionalRollback
                : BackgroundJobCapacityCleanupEvidence.NeverStarted);
            cleanup.SourceOwner.Should().BeSameAs(receipt.SourceOwner);
            cleanup.Envelope.Should().BeSameAs(receipt.Envelope);
            cleanup.Receipt.Should().BeSameAs(lostAdmissionAcknowledgement ? null : receipt);
            (await invocation.PrepareCapacityCleanupAsync(execution)).Should().BeSameAs(cleanup);
            using (cleanup.SourceOwner.Enter()) cleanup.SourceOwner.RequireFreshScopeReady();
            fixture.Control.Snapshot.Executions.Should().Be(1, "a local witness does not finish the control checkpoint");
            invocation.Token.IsCancellationRequested.Should().BeTrue();
            var privateHandle = () => invocation.Token.WaitHandle;
            privateHandle.Should().NotThrow("the invocation source remains owned through the later checkpoint");
        }
        finally
        {
            release.Set();
            if (preparation is not null) await preparation.WaitAsync(TestWait);
            await callback.DisposeAsync();
            await invocation.CompleteAsync(await execution.JoinAsync(true).WaitAsync(TestWait)).WaitAsync(TestWait);
        }

        fixture.Control.Snapshot.Executions.Should().Be(0);
        var reopen = () => receipt.SourceOwner.Enter();
        reopen.Should().Throw<InvalidOperationException>("final cleanup seals the same original owner");
    }

    [Fact]
    public async Task Missing_business_task_cannot_mint_never_started_after_the_execution_entry_was_claimed()
    {
        await using var fixture = new Fixture();
        var (execution, invocation, _) = await fixture.BeginAsync(lostAdmissionAcknowledgement: false);
        execution.Gate.TryStartExecution().Should().BeTrue();
        try
        {
            (await invocation.PrepareCapacityCleanupAsync(execution).WaitAsync(TestWait)).Should().BeNull();
            fixture.Control.Snapshot.Executions.Should().Be(1);
        }
        finally
        {
            await invocation.CompleteAsync(await execution.JoinAsync(true).WaitAsync(TestWait)).WaitAsync(TestWait);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly BackgroundJobObservation _observation;
        private readonly PrivilegedDataSource _source = new(() =>
            throw new InvalidOperationException("This memory-only fixture must not open a database source."));
        private readonly HangfireGenerationWork _generation;
        internal BackgroundJobControl Control { get; }

        internal Fixture()
        {
            var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
            var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
            var bounds = new BackgroundJobRuntimeOptions();
            _observation = new(definitions, TimeProvider.System);
            var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
                new(definitions, definitions, new AuditCatalog([new BackgroundJobAuditCatalogSource()])), bounds, _observation);
            Control = new(_source, definitions, executor, gate, bounds, new SystemGuidFactory(), TimeProvider.System);
            _generation = new(new("probe", BackgroundJobWorkerPoolKind.Transactional, 1, ["probe"]), gate);
        }

        internal async Task<(BackgroundJobExecutionControl Execution, HangfireGenerationWork.Invocation Invocation,
            ConfirmedStartReceipt Receipt)> BeginAsync(bool lostAdmissionAcknowledgement)
        {
            var original = JobAdmissionFixtureReceipt.Create("tenancy.probe.v1", TenantId.From(Guid.CreateVersion7()),
                null, BackgroundJobExecutionStage.Transactional);
            var invocation = _generation.TryBegin() ?? throw new InvalidOperationException("Invocation admission failed.");
            var execution = Control.TryBegin(original.Claim, invocation) ?? throw new InvalidOperationException("Control admission failed.");
            var receipt = JobAdmissionFixtureReceipt.Create(original.Envelope.DefinitionKey, original.Envelope.TenantId,
                original.Envelope.OrganizationId, original.Stage, execution.AdmissionGate, original.Claim,
                bindRun: false, capacityGroup: "probe", acknowledgeAdmission: !lostAdmissionAcknowledgement);
            invocation.HoldExecution(execution);
            var admitted = lostAdmissionAcknowledgement ? null : receipt;
            var admission = Task.FromResult(new JobStartExecutionResult(lostAdmissionAcknowledgement
                ? JobStartExecutionOutcome.Indeterminate : JobStartExecutionOutcome.Started, admitted, false, receipt.SourceOwner)
            { ValidatedEnvelope = receipt.Envelope });
            invocation.HoldAdmission(admission);
            execution.HoldAdmission(admission);
            var publication = (TaskCompletionSource<ConfirmedStartReceipt?>)(typeof(BackgroundJobStartPublication)
                .GetField("_completion", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(execution.Publication)
                ?? throw new InvalidOperationException("The publication fixture is missing."));
            publication.SetResult(admitted);
            await execution.WaitForPublicationAsync().WaitAsync(TestWait);
            return (execution, invocation, receipt);
        }

        public async ValueTask DisposeAsync()
        {
            _generation.CloseStarts();
            Control.RequestStop();
            await Task.WhenAll(_generation.Completion, Control.Completion).WaitAsync(TestWait);
            await _source.DisposeAsync();
            _observation.Dispose();
        }
    }
}

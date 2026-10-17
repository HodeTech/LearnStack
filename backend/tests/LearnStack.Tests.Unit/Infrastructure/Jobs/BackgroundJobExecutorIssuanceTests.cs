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

public sealed class BackgroundJobExecutorIssuanceTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Stop_before_registered_admission_settles_publication_and_joins_the_actual_invocation()
    {
        await using var fixture = new Fixture();
        var synthetic = JobAdmissionFixtureReceipt.Create("tenancy.probe.v1", TenantId.From(Guid.CreateVersion7()),
            null, BackgroundJobExecutionStage.Transactional);
        var (execution, work) = fixture.Begin(synthetic.Claim);
        execution.CloseExecution();
        var actual = fixture.Executor.ExecutePublishedAsync(execution.Claim, execution.Publication,
            execution.AdmissionGate, null, work.Token);
        execution.HoldAdmission(actual);
        work.HoldAdmission(actual);
        var result = await actual.WaitAsync(TestWait);
        result.Outcome.Should().Be(JobStartExecutionOutcome.Canceled);
        result.Receipt.Should().BeNull();
        result.CleanupFailed.Should().BeFalse();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        (await execution.Publication.Completion.WaitAsync(TestWait)).Should().BeNull();
        await execution.WaitForPublicationAsync().WaitAsync(TestWait);
        fixture.Scopes.DidNotReceive().CreateScope();
        (await fixture.Control.FinishAdmissionAsync(execution, result)).Should().BeTrue();
        (await execution.JoinAsync(true).WaitAsync(TestWait)).Should().BeTrue();
        execution.CleanupFault.Should().BeFalse();
        await work.CompleteAsync(true).WaitAsync(TestWait);
        fixture.Control.Snapshot.Executions.Should().Be(0);
    }

    [Fact]
    public async Task Stop_before_grouped_business_entry_keeps_positive_unused_cleanup_reachable()
    {
        await using var fixture = new Fixture();
        var synthetic = JobAdmissionFixtureReceipt.Create("tenancy.probe.v1", TenantId.From(Guid.CreateVersion7()),
            null, BackgroundJobExecutionStage.Transactional);
        var (execution, work) = fixture.Begin(synthetic.Claim);
        var receipt = JobAdmissionFixtureReceipt.Create(synthetic.Envelope.DefinitionKey, synthetic.Envelope.TenantId,
            null, synthetic.Stage, execution.AdmissionGate, synthetic.Claim, bindRun: false, capacityGroup: "probe");
        var admission = Task.FromResult(new JobStartExecutionResult(JobStartExecutionOutcome.Started, receipt, false,
            receipt.SourceOwner));
        execution.HoldAdmission(admission);
        work.HoldAdmission(admission);
        // Synthetic ACK publication only; no database claim is made by this memory fixture.
        var signal = (TaskCompletionSource<ConfirmedStartReceipt?>)(typeof(BackgroundJobStartPublication)
            .GetField("_completion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(execution.Publication)!);
        signal.SetResult(receipt);
        await execution.WaitForPublicationAsync().WaitAsync(TestWait);
        execution.Identity.Should().NotBeNull();
        execution.Gate.IsClosed.Should().BeFalse("the acknowledged entry is eligible before the exact stop race");
        execution.CloseExecution();
        var actual = fixture.Executor.ExecuteBusinessAsync(receipt, execution.Gate, work.Token);
        execution.HoldBusiness(actual);
        work.HoldBusiness(actual);
        var result = await actual.WaitAsync(TestWait);
        result.CleanupFailed.Should().BeFalse();
        result.ExecutionIssued.Should().BeFalse();
        result.KnownRollback.Should().BeFalse("an unissued root has no physical rollback history to borrow");
        result.CommitStarted.Should().BeFalse();
        result.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.RetainOwnership);
        fixture.Scopes.DidNotReceive().CreateScope();
        execution.Gate.TrySealUnusedExecution().Should().BeTrue("the refused root never consumed its independent entry CAS");
        var cleanup = await work.PrepareCapacityCleanupAsync(execution).WaitAsync(TestWait);
        cleanup.Should().NotBeNull("positive actual joins and unused root proof remain available to the real checkpoint");
        cleanup!.Evidence.Should().Be(BackgroundJobCapacityCleanupEvidence.TransactionalRollback,
            "the completed owned business result positively reports no issued COMMIT or opened scope");
        cleanup.SourceOwner.Should().BeSameAs(receipt.SourceOwner);
        execution.CleanupFault.Should().BeFalse();
        (await execution.JoinAsync(true).WaitAsync(TestWait)).Should().BeTrue();
        await work.CompleteAsync(true).WaitAsync(TestWait);
        fixture.Control.Snapshot.Executions.Should().Be(0);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PrivilegedDataSource _source = new(() =>
            throw new InvalidOperationException("This memory-only fixture cannot resolve a database."));
        private readonly HangfireGenerationWork _generation;
        internal IServiceScopeFactory Scopes { get; } = Substitute.For<IServiceScopeFactory>();
        internal JobExecutor Executor { get; }
        internal BackgroundJobControl Control { get; }

        internal Fixture()
        {
            var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
            var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
            var options = new BackgroundJobRuntimeOptions();
            Executor = new(Scopes, Substitute.For<ITenantContextAccessor>(),
                new(definitions, definitions, new AuditCatalog([new BackgroundJobAuditCatalogSource()])), options);
            Control = new(_source, definitions, Executor, gate, options, new SystemGuidFactory(), TimeProvider.System);
            _generation = new(definitions.Infrastructure.Pools["transactional"], gate);
        }

        internal (BackgroundJobExecutionControl Execution, HangfireGenerationWork.Invocation Work) Begin(BackgroundJobRoutingClaim claim)
        {
            var work = _generation.TryBegin() ?? throw new InvalidOperationException("The owned invocation was not admitted.");
            var execution = Control.TryBegin(claim, work) ?? throw new InvalidOperationException("The owned execution was not registered.");
            work.HoldExecution(execution);
            return (execution, work);
        }

        public async ValueTask DisposeAsync()
        {
            _generation.CloseStarts();
            Control.RequestStop();
            try { await Task.WhenAll(_generation.Completion, Control.Completion).WaitAsync(TestWait); }
            catch (TimeoutException) when (Control.Snapshot.Executions != 0)
            {
                // Preserve the primary failed assertion when a deliberately broken
                // memory owner cannot join. Every passing path asserts zero owners;
                // this unopened fixture has no database or application resources.
            }
            await _source.DisposeAsync();
        }
    }
}

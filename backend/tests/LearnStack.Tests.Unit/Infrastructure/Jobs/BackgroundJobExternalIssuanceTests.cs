using FluentAssertions;
using System.Reflection;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Unit.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

/// <summary>Preconstruction memory refusals only; no provider success or durability is claimed.</summary>
public sealed class BackgroundJobExternalIssuanceTests : IDisposable
{
    private readonly List<BackgroundJobObservation> _observations = [];
    public void Dispose() { foreach (var observation in _observations) observation.Dispose(); }

    [Theory]
    [InlineData("unacknowledged")]
    [InlineData("borrowed_ack")]
    [InlineData("stopped")]
    [InlineData("cancelled")]
    public async Task Provider_scope_is_not_constructed_from_unacknowledged_or_stopped_original_issuance(string state)
    {
        var scopes = Substitute.For<IServiceScopeFactory>();
        var accessor = new StaticTenantContextAccessor(UnresolvedTenantContext.Instance);
        var executor = Executor(scopes, accessor);
        var arm = UnacknowledgedArm();
        if (state == "borrowed_ack")
        {
            // Synthetic gate history is distinct from this command's ACK.
            // Exact ACK guard discrimination uses a real trusted-read PG control.
            arm.Gate.TryIssue().Should().BeTrue();
            ((bool)typeof(BackgroundJobCommitGate).GetMethod("TryBeginCommit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(arm.Gate, null)!).Should().BeTrue();
            typeof(BackgroundJobCommitGate).GetMethod("RecordCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(arm.Gate, null);
            arm.Gate.CommitAcknowledged.Should().BeTrue();
            arm.CommitAcknowledged.Should().BeFalse();
        }
        if (state == "stopped") arm.Receipt.Run.Close();
        using var cancellation = new CancellationTokenSource();
        if (state == "cancelled") await cancellation.CancelAsync();
        var pending = new JobExternalCheckpointResult(JobExternalCheckpointStatus.Refused, arm, null,
            state == "borrowed_ack", false, []);
        // No successful read is fabricated for these negative memory controls.
        var result = await executor.ExecuteExternalAsync(null!, pending, cancellation.Token);
        result.Status.Should().Be(state is "unacknowledged" or "borrowed_ack"
            ? JobExternalExecutionStatus.ProviderFault : JobExternalExecutionStatus.EntryRefused);
        result.Observation.Should().BeNull();
        result.CleanupFailed.Should().BeFalse();
        result.RetainedCleanup.Should().BeEmpty();
        scopes.DidNotReceive().CreateScope();
        JobApplicationSourceOwner.IsExternal.Should().BeFalse();
        accessor.Current.Should().BeSameAs(UnresolvedTenantContext.Instance);
        arm.Receipt.Run.TrySealUnusedExternalExecute().Should().BeTrue();
    }

    [Fact]
    public async Task Even_preconstruction_stop_reports_a_failed_actual_context_restore_instead_of_returning_early_success()
    {
        var scopes = Substitute.For<IServiceScopeFactory>();
        var executor = Executor(scopes, new ThrowingRestoreAccessor());
        var arm = UnacknowledgedArm();
        arm.Receipt.Run.Close();
        var pending = new JobExternalCheckpointResult(JobExternalCheckpointStatus.Refused, arm, null, false, false, []);
        var result = await executor.ExecuteExternalAsync(null!, pending, CancellationToken.None);
        result.Status.Should().Be(JobExternalExecutionStatus.EntryRefused);
        result.Observation.Should().BeNull();
        result.CleanupFailed.Should().BeTrue();
        result.RetainedCleanup.Should().ContainSingle();
        scopes.DidNotReceive().CreateScope();
        Action reopen = () => _ = arm.Receipt.SourceOwner.Enter();
        reopen.Should().Throw<InvalidOperationException>();
    }

    private static RecordBackgroundJobArmCommand UnacknowledgedArm()
    {
        var receipt = JobAdmissionFixtureReceipt.Create("tenancy.probe.v1", TenantId.From(Guid.CreateVersion7()),
            null, BackgroundJobExecutionStage.Prepare, capacityGroup: "tenancy.resource");
        var fingerprint = new string('a', 64);
        var operation = BackgroundJobExternalIdentity.OperationId(receipt.Envelope.JobId, fingerprint);
        return new RecordBackgroundJobArmCommand(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm),
            fingerprint, operation);
    }

    private JobExecutor Executor(IServiceScopeFactory scopes, ITenantContextAccessor accessor)
    {
        var registry = BackgroundJobDefinitionRegistry.CreateEmpty();
        var observation = new BackgroundJobObservation(registry, TimeProvider.System);
        _observations.Add(observation);
        return new(scopes, accessor, new BackgroundJobExecutionAdmission(registry, registry,
            new AuditCatalog([new BackgroundJobAuditCatalogSource()])), new BackgroundJobRuntimeOptions(), observation);
    }

    private sealed class ThrowingRestoreAccessor : ITenantContextAccessor
    {
        public ITenantContext? Current
        {
            get => null;
            set => throw new InvalidOperationException("Synthetic context restoration failure.");
        }
    }
}

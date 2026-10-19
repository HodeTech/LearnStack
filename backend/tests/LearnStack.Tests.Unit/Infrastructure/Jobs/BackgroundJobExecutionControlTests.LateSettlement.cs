using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed partial class BackgroundJobExecutionControlTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("business")]
    [InlineData("old_confirmation")]
    [InlineData("no_plan_confirmation")]
    [InlineData("false_disposition")]
    public async Task Late_classifier_requires_positive_join_and_the_current_exact_flight_not_old_or_missing_tasks(string mode)
    {
        await using var source = new PrivilegedDataSource(() => throw new InvalidOperationException("This classifier fixture activates no source."));
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        using var observation = new BackgroundJobObservation(definitions, TimeProvider.System);
        var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
            new(definitions, definitions, new AuditCatalog([new BackgroundJobAuditCatalogSource()])), new(), observation);
        var owner = new BackgroundJobControl(source, definitions, executor, gate, new(), new SystemGuidFactory(), TimeProvider.System);
        var generation = new HangfireGenerationWork(new("probe", BackgroundJobWorkerPoolKind.Transactional, 1, ["probe"]), gate);
        var current = await BeginAsync(owner, generation);
        try
        {
            if (mode == "business")
                current.Execution.HoldBusiness(Task.FromResult(KnownRollback(current.Receipt, current.Execution.Gate)));
            current.Execution.HasSettledStage(BackgroundJobInvocationStage.Transactional).Should().BeFalse("a completed result is not a positive observer/source join");
            (await current.Execution.JoinAsync(operationCleanupConfirmed: true)).Should().BeTrue();
            if (mode == "business")
            {
                current.Execution.HasSettledStage(BackgroundJobInvocationStage.Transactional).Should().BeTrue();
                current.Execution.HasSettledStage(BackgroundJobInvocationStage.Prepare).Should().BeFalse();
                current.Execution.HasSettledStage(BackgroundJobInvocationStage.Finalize).Should().BeFalse();
            }
            else if (mode == "missing")
                foreach (var stage in Enum.GetValues<BackgroundJobInvocationStage>())
                    current.Execution.HasSettledStage(stage).Should().BeFalse("arming a stage does not issue its task: {0}", stage);
            else
            {
                // Dirty memory-only task selection after a real clean observer
                // join; these objects claim no database ACK/recovery authority.
                var resources = new BackgroundJobControlResources();
                await resources.CleanupAsync();
                var old = new OwnedBackgroundJobControl<BackgroundJobExternalRecoveryReceipt>(null, resources, []);
                Set("_recoveryConfirmation", Task.FromResult(old));
                current.Execution.HasSettledStage(BackgroundJobInvocationStage.RecoveryConfirmation).Should().BeTrue();
                if (mode == "false_disposition")
                {
                    Set("_externalFinalizeDisposition", Task.FromResult(false));
                    current.Execution.HasSettledStage(BackgroundJobInvocationStage.RecoveryDisposition).Should().BeFalse();
                    Set("_externalFinalizeDisposition", Task.FromResult(true));
                    current.Execution.HasSettledStage(BackgroundJobInvocationStage.RecoveryDisposition).Should().BeTrue();
                }
                else
                {
                    var change = new OwnedBackgroundJobControl<BackgroundJobControlChange>(null, resources, []);
                    Set(mode == "old_confirmation" ? "_externalRetry" : "_noPlanTerminalStatus", Task.FromResult(change));
                    current.Execution.HasSettledStage(BackgroundJobInvocationStage.RecoveryConfirmation).Should().BeFalse(
                        "the expected current probe is missing, even though old acquisition confirmation is clean");
                    var currentProbe = new OwnedBackgroundJobControl<BackgroundJobProbeResult>(null, resources, []);
                    Set(mode == "old_confirmation" ? "_externalRetryProbe" : "_noPlanTerminalProbe", Task.FromResult(currentProbe));
                    current.Execution.HasSettledStage(BackgroundJobInvocationStage.RecoveryConfirmation).Should().BeTrue();
                }
                void Set(string name, object value) => typeof(BackgroundJobExecutionControl)
                    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(current.Execution, value);
            }
            source.IsValueCreated.Should().BeFalse();
        }
        finally
        {
            await current.Invocation.CompleteAsync(true);
            generation.CloseStarts();
            owner.RequestStop();
            await Task.WhenAll(generation.Completion, owner.Completion).WaitAsync(TestWait);
        }
    }
}

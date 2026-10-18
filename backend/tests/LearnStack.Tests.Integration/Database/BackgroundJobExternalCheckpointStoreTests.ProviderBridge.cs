using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData("unacknowledged")]
    [InlineData("borrowed_ack")]
    [InlineData("duplicate")]
    [InlineData("stopped")]
    [InlineData("cancelled")]
    [InlineData("foreign_read")]
    [InlineData("actual_canary")]
    public async Task Execute_bridge_requires_the_exact_clean_acknowledged_arm_and_trusted_read_before_scope_construction(string state)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var construction = new ProviderConstructionProbe();
        await using var host = BridgeCanaryHost(database, construction);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        read.Handle!.Snapshot.ExecuteIntentRecorded.Should().BeFalse("the normal pre-Arm app read is legitimate input");
        var gate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        JobExternalCheckpointResult arm;
        if (state == "unacknowledged")
        {
            var snapshot = read.Handle.Snapshot;
            arm = new(JobExternalCheckpointStatus.Refused, new(original, gate, snapshot.PlanFingerprint!, snapshot.OperationId!.Value),
                null, false, false, []);
        }
        else
        {
            arm = await executor.RecordExternalArmAsync(original, read.Handle, gate, CancellationToken.None);
            arm.CommitAcknowledged.Should().BeTrue();
        }
        if (state == "borrowed_ack")
        {
            // Deliberately substituted test-only result: this different command
            // asserts Issued to isolate its absent owner ACK, with a REAL read.
            // The production writer never publishes this fabricated shape.
            var snapshot = read.Handle.Snapshot;
            arm = new(JobExternalCheckpointStatus.Completed, new(original, gate, snapshot.PlanFingerprint!, snapshot.OperationId!.Value),
                null, true, false, []);
            gate.CommitAcknowledged.Should().BeTrue();
            arm.CommitAcknowledged.Should().BeFalse();
        }
        if (state == "duplicate")
        {
            arm = await executor.RecordExternalArmAsync(original, read.Handle, gate, CancellationToken.None);
            arm.Status.Should().Be(JobExternalCheckpointStatus.Refused);
            arm.Issued.Should().BeFalse();
        }
        if (state == "stopped") original.Run.Close();
        if (state == "foreign_read")
        {
            var other = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
            var foreign = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, other));
            read = await executor.ReadExternalStateAsync(foreign, CancellationToken.None);
            read.Handle.Should().NotBeNull();
        }
        using var cancellation = new CancellationTokenSource();
        if (state == "cancelled") await cancellation.CancelAsync();
        var (bridge, scopes) = ObservedProviderBridge(host.Services);
        using var metrics = new BackgroundJobStageProbe(host.Services.GetRequiredService<BackgroundJobObservation>());
        var result = await bridge.ExecuteExternalAsync(read.Handle!, arm, cancellation.Token);
        result.Status.Should().Be(state is "stopped" or "cancelled" ? JobExternalExecutionStatus.EntryRefused
            : JobExternalExecutionStatus.ProviderFault);
        result.Observation.Should().BeNull();
        result.ProviderIssued.Should().BeFalse("a scope or adapter constructor fault is not an issued provider operation");
        metrics.Points.Should().NotContain(point => point.Name == "learnstack_job_unknown_outcome_total" || point.Name == "learnstack_job_failed_total");
        metrics.Duration("execute").Tags["outcome"].Should().Be(state is "stopped" or "cancelled" ? "Refused" : "UnissuedProviderFault");
        result.CleanupFailed.Should().BeFalse();
        result.RetainedCleanup.Should().BeEmpty();
        scopes.Calls.Should().Be(state == "actual_canary" ? 1 : 0);
        construction.Calls.Should().Be(state == "actual_canary" ? 1 : 0);
        if (state == "actual_canary")
        {
            scopes.ExternalDenialObserved.Should().BeTrue();
            scopes.ContextWasNull.Should().BeTrue();
            scopes.NoApplicationSource.Should().BeTrue();
            construction.ConstructorCanaryHit.Should().BeTrue();
            construction.ExternalDenialObserved.Should().BeTrue();
            // The unchanged fixture adapter throws in its constructor. This is
            // an entry tripwire, never a successful provider-method proof.
        }
        original.Run.TrySealUnusedExternalExecute().Should().BeTrue("none of these cases reached the actual typed Execute call");
        original.SourceOwner.CleanupConfirmed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Theory]
    [InlineData("absent_plan")]
    [InlineData("absent_intent")]
    [InlineData("no_start")]
    [InlineData("durable_outcome")]
    public async Task Reconcile_bridge_uses_actual_recovery_read_and_never_constructs_provider_for_already_decidable_history(string history)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var construction = new ProviderConstructionProbe();
        await using var host = BridgeCanaryHost(database, construction);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        if (history is "absent_intent" or "durable_outcome")
            (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
                .CommitAcknowledged.Should().BeTrue();
        if (history == "durable_outcome")
        {
            var originalRead = await executor.ReadExternalStateAsync(original, CancellationToken.None);
            originalRead.Handle.Should().NotBeNull();
            (await executor.RecordExternalArmAsync(original, originalRead.Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
            BackgroundJobRetainedExternalObservation report;
            using (BackgroundJobExecutionAdmission.EnterExternal())
                report = BackgroundJobRetainedExternalObservation.Capture(new(original.Envelope.JobId, original.AttemptId,
                    originalRead.Handle!.Snapshot.OperationId!.Value, original.Deadline), originalRead.Handle.Snapshot.PlanFingerprint!,
                    BackgroundJobExternalResult.Failed(new WriterOutcome(Guid.NewGuid()),
                        new BackgroundJobQuiescenceEvidence(Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced),
                        BackgroundJobExternalFailurePolicy.TransientRepeatable));
            (await executor.RecordExternalOutcomeAsync(original, originalRead.Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), report, CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
        }
        original.Run.Close();
        await RevokeForDriverRecoveryAsync(database, job);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
            original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        if (history == "no_start")
        {
            (await executor.RecordExternalNoStartAsync(recovery, read.Handle!,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
            read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
            read.Handle.Should().NotBeNull();
            read.Handle!.Snapshot.NoStartRecorded.Should().BeTrue();
        }
        if (history == "durable_outcome")
        {
            read.Handle!.Snapshot.OutcomeState.Should().Be(BackgroundJobExternalState.Failed);
            read.Handle.Snapshot.FailurePolicy.Should().Be(BackgroundJobExternalFailurePolicy.TransientRepeatable);
        }
        else read.Handle!.Snapshot.ExecuteIntentRecorded.Should().BeFalse();
        var (bridge, scopes) = ObservedProviderBridge(host.Services);
        var result = await bridge.ReconcileExternalAsync(recovery, read.Handle!, CancellationToken.None);
        result.Status.Should().Be(JobExternalExecutionStatus.EntryRefused);
        result.Observation.Should().BeNull("no provider report is fabricated for already retained or absent-intent facts");
        result.CleanupFailed.Should().BeFalse();
        result.RetainedCleanup.Should().BeEmpty();
        scopes.Calls.Should().Be(0);
        construction.Calls.Should().Be(0);
        recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    private static (JobExecutor Executor, ProviderScopeObserver Scopes) ObservedProviderBridge(IServiceProvider services)
    {
        var accessor = services.GetRequiredService<ITenantContextAccessor>();
        var observer = new ProviderScopeObserver(services.GetRequiredService<IServiceScopeFactory>(), accessor);
        return (new(observer, accessor, services.GetRequiredService<BackgroundJobExecutionAdmission>(),
            services.GetRequiredService<BackgroundJobRuntimeOptions>(), services.GetRequiredService<BackgroundJobObservation>()), observer);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> BridgeCanaryHost(
        DisposableSchemaDatabase database, ProviderConstructionProbe probe) => RecoveryHost(database, preparing: true,
            configure: services =>
            {
                var definition = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!;
                Fixture.Registry.Infrastructure.TryGetAdapter(definition.ProfileRegistration.ExternalPortType!, out var binding)
                    .Should().BeTrue();
                // Test-only observation invokes the unchanged exact frozen class.
                // The shared fixture ordinarily needs no adapter DI registration.
                services.AddTransient(binding!.PortType, provider =>
                {
                    probe.Calls++;
                    probe.ExternalDenialObserved = JobApplicationSourceOwner.IsExternal;
                    try { return ActivatorUtilities.CreateInstance(provider, binding.ImplementationType); }
                    catch (Exception failure)
                    {
                        probe.ConstructorCanaryHit = failure.GetBaseException() is InvalidOperationException
                        { Message: "Admission constructed an external adapter." };
                        throw;
                    }
                });
            });

    private sealed class ProviderConstructionProbe
    {
        internal int Calls { get; set; }
        internal bool ConstructorCanaryHit { get; set; }
        internal bool ExternalDenialObserved { get; set; }
    }

    private sealed class ProviderScopeObserver(IServiceScopeFactory inner, ITenantContextAccessor accessor) : IServiceScopeFactory
    {
        internal int Calls { get; private set; }
        internal bool ExternalDenialObserved { get; private set; }
        internal bool ContextWasNull { get; private set; }
        internal bool NoApplicationSource { get; private set; }
        public IServiceScope CreateScope()
        {
            Calls++;
            ExternalDenialObserved = JobApplicationSourceOwner.IsExternal;
            ContextWasNull = accessor.Current is null;
            NoApplicationSource = JobApplicationSourceOwner.Current is null;
            return inner.CreateScope();
        }
    }
}

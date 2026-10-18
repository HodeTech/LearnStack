using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_writers_use_distinct_app_transactions_and_only_actual_read_causal_facts(bool freshRecovery)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var parent = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var actor = Guid.CreateVersion7();
        var incoming = Guid.NewGuid();
        var trace = $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup,
            profile: "external", parentJob: parent.Value, causalActor: actor, incomingEvent: incoming, traceparent: trace);
        var probe = new WriterDriverProbe();
        await using var host = WriterDriverHost(database, probe);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        var armGate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        var arm = await executor.RecordExternalArmAsync(original, read.Handle!, armGate, CancellationToken.None);
        arm.Status.Should().Be(JobExternalCheckpointStatus.Completed);
        arm.Arm.Should().NotBeNull();
        arm.Arm!.Gate.Should().BeSameAs(armGate);
        arm.CommitAcknowledged.Should().BeTrue();
        arm.CleanupFailed.Should().BeFalse();
        arm.RetainedCleanup.Should().BeEmpty();
        var duplicate = await executor.RecordExternalArmAsync(original, read.Handle!, armGate, CancellationToken.None);
        duplicate.Status.Should().Be(JobExternalCheckpointStatus.Refused);
        duplicate.Arm.Should().NotBeSameAs(arm.Arm);
        duplicate.Issued.Should().BeFalse();
        duplicate.CommitStarted.Should().BeFalse("a rejected second command cannot borrow the first command's COMMIT history");
        duplicate.CommitAcknowledged.Should().BeFalse();
        duplicate.KnownRollback.Should().BeFalse();
        duplicate.CommitUncertain.Should().BeFalse();
        duplicate.CleanupFailed.Should().BeFalse();
        arm.CommitAcknowledged.Should().BeTrue("the original physical acknowledgement remains unchanged");

        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        BackgroundJobExternalRecoveryBinding? recovery = null;
        if (freshRecovery)
        {
            original.Run.Close();
            await RevokeForDriverRecoveryAsync(database, job);
            var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
                original.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            recovery = BindRecovery(acquired, runtime);
            read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
            read.Handle.Should().NotBeNull();
        }
        var observation = DriverObservation(original, read.Handle!.Snapshot);
        var gate = (recovery?.Run ?? original.Run).OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        var result = recovery is null
            ? await executor.RecordExternalOutcomeAsync(original, read.Handle, gate, observation, CancellationToken.None)
            : await executor.RecordExternalOutcomeAsync(recovery, read.Handle, gate, observation, CancellationToken.None);
        result.Status.Should().Be(JobExternalCheckpointStatus.Completed);
        result.Kind.Should().Be(BackgroundJobCheckpointKind.Outcome);
        result.CommitAcknowledged.Should().BeTrue();
        result.KnownRollback.Should().BeFalse();
        result.CommitUncertain.Should().BeFalse();
        result.CleanupFailed.Should().BeFalse();
        result.RetainedCleanup.Should().BeEmpty();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        var frames = probe.Frames.ToArray();
        frames.Should().HaveCount(freshRecovery ? 4 : 3);
        frames.Select(frame => frame.Transaction).Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(frames.Length);
        foreach (var frame in frames)
        {
            frame.Connection.State.Should().Be(ConnectionState.Closed);
            frame.Context.JobId.Should().Be(job);
            frame.Context.TenantId.Value.Should().Be(SchemaFixture.TenantA);
            frame.Context.UserId.Should().Be(UserId.SystemActor);
            if (frame.RecoveryRead)
            {
                frame.Context.ParentJobId.Should().BeNull();
                frame.Context.CausalActorUserId.Should().BeNull();
                frame.Context.IncomingEventId.Should().BeNull();
                frame.Context.CorrelationId.Should().BeNull();
            }
            else
            {
                frame.Context.ParentJobId!.Value.Should().Be(parent);
                frame.Context.CausalActorUserId!.Value.Value.Should().Be(actor);
                frame.Context.IncomingEventId.Should().Be(incoming);
                frame.Context.CorrelationId.Should().Be(trace);
            }
        }
        var stored = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job);
        stored!.Phase.Should().Be("finalize_pending");
        stored.NormalAttempts.Should().Be(1);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Theory]
    [InlineData("arm", false)]
    [InlineData("arm", true)]
    [InlineData("outcome", false)]
    [InlineData("outcome", true)]
    public async Task Named_writer_keeps_physical_ack_and_exact_resources_through_held_or_failed_scope_cleanup(
        string kind, bool throwOnCleanup)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var probe = new WriterDriverProbe();
        await using var host = WriterDriverHost(database, probe);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        if (kind == "outcome")
        {
            var prior = await executor.RecordExternalArmAsync(original, read.Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
            prior.CommitAcknowledged.Should().BeTrue();
        }
        probe.HeldKind = kind;
        probe.ThrowOnCleanup = throwOnCleanup;
        var gate = original.Run.OpenBoundary(kind == "arm" ? BackgroundJobCheckpointKind.Arm : BackgroundJobCheckpointKind.Outcome);
        var actual = kind == "arm"
            ? executor.RecordExternalArmAsync(original, read.Handle!, gate, CancellationToken.None)
            : executor.RecordExternalOutcomeAsync(original, read.Handle!, gate,
                DriverObservation(original, read.Handle!.Snapshot), CancellationToken.None);
        Exception? premiseFailure = null;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            gate.CommitAcknowledged.Should().BeTrue();
            actual.IsCompleted.Should().BeFalse();
            original.SourceOwner.CleanupConfirmed.Should().BeFalse();
        }
        catch (Exception failure) { premiseFailure = failure; }
        finally { probe.Release.TrySetResult(); }
        var result = await actual.WaitAsync(TimeSpan.FromSeconds(15));
        premiseFailure.Should().BeNull();
        result.Status.Should().Be(JobExternalCheckpointStatus.Completed);
        result.Gate.Should().BeSameAs(gate);
        if (kind == "arm") result.Arm.Should().BeSameAs(probe.Arm);
        else result.Outcome.Should().BeSameAs(probe.Outcome);
        result.CommitAcknowledged.Should().BeTrue();
        result.KnownRollback.Should().BeFalse();
        result.CommitUncertain.Should().BeFalse();
        result.CleanupFailed.Should().Be(throwOnCleanup);
        probe.HeldConnection!.State.Should().Be(ConnectionState.Closed);
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        if (throwOnCleanup)
        {
            result.RetainedCleanup.Should().Contain(owner => ReferenceEquals(owner.Unit, probe.HeldUnit) && owner.Scope != null);
            original.Run.IsClosed.Should().BeTrue();
            Action reuse = () => _ = read.Handle!.Require(original);
            reuse.Should().Throw<InvalidOperationException>();
        }
        else
        {
            result.RetainedCleanup.Should().BeEmpty();
            original.Run.IsClosed.Should().BeFalse("successful checkpoint cleanup does not stop the shared external run");
        }
    }

    [Fact]
    public async Task Named_recovery_no_start_uses_its_exact_read_without_fabricating_provider_facts()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, original);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
            original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        var result = await executor.RecordExternalNoStartAsync(recovery, read.Handle!,
            recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
        result.Status.Should().Be(JobExternalCheckpointStatus.Completed);
        result.Outcome!.IsNoStart.Should().BeTrue();
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupFailed.Should().BeFalse();
        var after = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        after.Handle.Should().NotBeNull();
        after.Handle!.Snapshot.NoStartRecorded.Should().BeTrue();
        after.Handle.Snapshot.ExecuteIntentRecorded.Should().BeFalse();
        after.Handle.Snapshot.PlanJson.Should().BeNull();
        after.Handle.Snapshot.OutcomeJson.Should().BeNull();
        after.Handle.Snapshot.FailurePolicy.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_writer_owns_activity_even_when_its_start_or_stop_observer_throws(bool throwOnStop)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var trace = ActivityTraceId.CreateRandom();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup,
            profile: "external", traceparent: $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        await using var host = RecoveryHost(database, preparing: true);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        Activity? captured = null;
        var stopped = false;
        var throws = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackgroundJobObservation.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "job.external_checkpoint" || activity.TraceId != trace) return;
                captured = activity;
                stopped = true;
                if (throwOnStop && Interlocked.CompareExchange(ref throws, 1, 0) == 0)
                    throw new InvalidOperationException("Explicit writer Activity stop fault.");
            },
        };
        EventHandler<ActivityChangedEventArgs> startObserver = (_, change) =>
        {
            if (throwOnStop || change.Current is not { OperationName: "job.external_checkpoint" } activity
                || activity.TraceId != trace || Interlocked.CompareExchange(ref throws, 1, 0) != 0) return;
            captured = activity;
            throw new InvalidOperationException("Explicit writer Activity start fault.");
        };
        ActivitySource.AddActivityListener(listener);
        var previous = Activity.Current;
        Activity.CurrentChanged += startObserver;
        JobExternalCheckpointResult result;
        try
        {
            result = await executor.RecordExternalArmAsync(original, read.Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        }
        finally { Activity.CurrentChanged -= startObserver; }
        throws.Should().Be(1, "the actual selected observer must have run");
        captured.Should().NotBeNull();
        stopped.Should().BeTrue("the writer retains its started Activity even if Start throws before returning");
        Activity.Current.Should().BeSameAs(previous);
        result.CommitAcknowledged.Should().Be(throwOnStop);
        result.Issued.Should().Be(throwOnStop);
        result.CleanupFailed.Should().Be(throwOnStop);
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        if (throwOnStop)
        {
            result.Status.Should().Be(JobExternalCheckpointStatus.Completed);
            result.RetainedCleanup.Should().Contain(owner => ReferenceEquals(owner.Activity, captured));
            original.Run.IsClosed.Should().BeTrue();
        }
        else
        {
            result.Status.Should().Be(JobExternalCheckpointStatus.Refused);
            result.CommitStarted.Should().BeFalse();
            result.CommitUncertain.Should().BeFalse();
            result.RetainedCleanup.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Named_writer_refuses_another_jobs_read_handle_before_spending_its_actual_gate()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = RecoveryHost(database, preparing: true);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var receipts = new List<ConfirmedStartReceipt>();
        var reads = new List<JobExternalReadHandle>();
        for (var index = 0; index < 2; index++)
        {
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
            var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
            (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
                .CommitAcknowledged.Should().BeTrue();
            var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
            read.Handle.Should().NotBeNull();
            receipts.Add(original);
            reads.Add(read.Handle!);
        }
        var gate = receipts[0].Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        var foreign = () => executor.RecordExternalArmAsync(receipts[0], reads[1], gate, CancellationToken.None);
        await foreign.Should().ThrowAsync<InvalidOperationException>();
        gate.CommitStarted.Should().BeFalse();
        gate.CommitAcknowledged.Should().BeFalse();
        var actual = await executor.RecordExternalArmAsync(receipts[0], reads[0], gate, CancellationToken.None);
        actual.Status.Should().Be(JobExternalCheckpointStatus.Completed);
        actual.Issued.Should().BeTrue("the foreign handle was rejected before this exact gate's one issuance");
        actual.CommitAcknowledged.Should().BeTrue();
        actual.CleanupFailed.Should().BeFalse();
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WriterDriverHost(
        DisposableSchemaDatabase database, WriterDriverProbe probe) => RecoveryHost(database, preparing: true, configure: services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<WriterDriverHeldCleanup>();
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ObserveWriterDriver<,>));
        });

    private static BackgroundJobRetainedExternalObservation DriverObservation(ConfirmedStartReceipt original,
        BackgroundJobExternalSnapshot snapshot)
    {
        using var denial = BackgroundJobExecutionAdmission.EnterExternal();
        return BackgroundJobRetainedExternalObservation.Capture(new(original.Envelope.JobId, original.AttemptId,
            snapshot.OperationId!.Value, original.Deadline), snapshot.PlanFingerprint!,
            BackgroundJobExternalResult.Succeeded(new WriterOutcome(Guid.NewGuid()),
                new BackgroundJobQuiescenceEvidence(Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced)));
    }

    private static async Task RevokeForDriverRecoveryAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id;
            UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
            """, ("id", job.Value));
    }

    private sealed record WriterOutcome(Guid ArtifactId) : IBackgroundJobOutcome;
    private sealed record WriterObservedFrame(DbConnection Connection, DbTransaction Transaction, JobTenantContext Context, bool RecoveryRead);
    private sealed class WriterDriverProbe
    {
        internal ConcurrentQueue<WriterObservedFrame> Frames { get; } = new();
        internal string? HeldKind { get; set; }
        internal bool ThrowOnCleanup { get; set; }
        internal RecordBackgroundJobArmCommand? Arm { get; set; }
        internal RecordBackgroundJobOutcomeCommand? Outcome { get; set; }
        internal IUnitOfWork? HeldUnit { get; set; }
        internal DbConnection? HeldConnection { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObserveWriterDriver<TRequest, TResponse>(WriterDriverProbe probe, IUnitOfWork unit,
        ITenantContextAccessor accessor, IServiceProvider services) : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (request is ReadBackgroundJobExternalStateQuery or RecordBackgroundJobArmCommand or RecordBackgroundJobOutcomeCommand)
            {
                await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction!);
                unit.Mode.Should().Be(request is ReadBackgroundJobExternalStateQuery ? TransactionMode.ReadOnly : TransactionMode.ReadWrite);
                probe.Frames.Enqueue(new(unit.Connection, unit.Transaction!, (JobTenantContext)accessor.Current!,
                    request is ReadBackgroundJobExternalStateQuery { Authority.IsRecovery: true }));
                if (request is RecordBackgroundJobArmCommand arm && probe.HeldKind == "arm")
                {
                    probe.Arm = arm;
                    services.GetRequiredService<WriterDriverHeldCleanup>().Observe(unit);
                }
                if (request is RecordBackgroundJobOutcomeCommand outcome && probe.HeldKind == "outcome")
                {
                    probe.Outcome = outcome;
                    services.GetRequiredService<WriterDriverHeldCleanup>().Observe(unit);
                }
            }
            return await next();
        }
    }

    private sealed class WriterDriverHeldCleanup(WriterDriverProbe probe) : IAsyncDisposable
    {
        internal void Observe(IUnitOfWork unit) => probe.HeldUnit = unit;
        public async ValueTask DisposeAsync()
        {
            probe.HeldConnection = probe.HeldUnit!.Connection;
            probe.Entered.TrySetResult();
            await probe.Release.Task;
            if (probe.ThrowOnCleanup) throw new InvalidOperationException("Explicit writer scope cleanup fault.");
        }
    }
}

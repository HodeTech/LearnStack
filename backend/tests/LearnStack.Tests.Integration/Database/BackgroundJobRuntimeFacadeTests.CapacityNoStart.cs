using System.Data;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_grouped_admission_never_started_releases_only_after_actual_scope_and_private_callback_join(bool loseAdmissionAck)
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds() with
        {
            SqlTimeout = TimeSpan.FromSeconds(5),
            TransactionTimeout = TimeSpan.FromSeconds(10),
        };
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: loseAdmissionAck ? 1 : int.MaxValue, holdCommitResponse: loseAdmissionAck);
        var state = new CapacityNoStartState();
        var provider = new CapacityEntitlementProvider(1);
        var roots = Fixture.RootConstructions;
        var calls = 0;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result<None>.Fail(BackgroundJobExecutionAdmission.Refused));
            },
        };
        BackgroundJobRuntime? boundRuntime = null;
        CancellationTokenRegistration privateCallback = default;
        var observation = new BackgroundJobAdmissionObservation
        {
            OnStart = (command, services) =>
            {
                // A legitimate later retry sees allowance zero. Capture only
                // the original native admission and its original scope.
                if (state.Command is not null) return Task.CompletedTask;
                proxy.CompletedCommits.Should().Be(1, "only the actual read transaction has committed before admission");
                state.Command = command;
                state.OriginalOwner = JobApplicationSourceOwner.Current!;
                state.OriginalUnit = services.GetRequiredService<IUnitOfWork>();
                var execution = NativeCommitExecution(boundRuntime!, command.Claim.JobId);
                state.Execution = execution;
                privateCallback = execution.Token.Register(() =>
                {
                    state.Callback.Entered.TrySetResult();
                    state.Callback.Release.Task.GetAwaiter().GetResult();
                    if (state.VerifyClosedBinding)
                    {
                        JobApplicationSourceOwner.Current.Should().BeSameAs(state.OriginalOwner);
                        var acquire = () => state.OriginalOwner!.BeginStandaloneAudit();
                        acquire.Should().Throw<InvalidOperationException>("the inherited original framework binding is closed");
                        var reenter = () => state.OriginalOwner!.Enter();
                        reenter.Should().Throw<InvalidOperationException>();
                    }
                    state.Callback.Joined.TrySetResult();
                });
                _ = services.GetRequiredService<CapacityNoStartAdmissionDisposal>();
                return Task.CompletedTask;
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(Fixture.CapacityRegistry(planLimit: LimitKeys.MaxOrganizations));
            services.RemoveAll<IEntitlementProvider>();
            services.AddSingleton<IEntitlementProvider>(provider);
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(state);
            observation.Configure(services);
            services.AddScoped<CapacityNoStartAdmissionDisposal>();
            services.AddTransient<IPipelineBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>,
                CapacityNoStartCheckpointObserver>();
        }, appConnectionString: new NpgsqlConnectionStringBuilder(proxy.ConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 100,
            CommandTimeout = 0,
            CancellationTimeout = 1000,
        }.ConnectionString);
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        var controlName = "capacity_no_start_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, controlName);
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, bounds, physical);
        boundRuntime = runtime;
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        var lockReleased = false;
        var verified = false;
        try
        {
            await runtime.StartAsync();
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            await EnsureQualificationStartAsync(runtime, source, job,
                loseAdmissionAck ? proxy.CommitResponseHeld : state.Disposal.Entered.Task);
            if (loseAdmissionAck)
            {
                await proxy.CommitResponseHeld.WaitAsync(TestWait);
                proxy.CompletedCommits.Should().Be(2, "the withheld response belongs to the real counted admission");
                locked = await locker.BeginTransactionAsync();
                await LockNativeJobAsync(locker, locked, job, CancellationToken.None);
            }
            try { await state.Disposal.Entered.Task.WaitAsync(TestWait); }
            catch (TimeoutException failure)
            {
                var capturedExecution = state.Execution;
                var actualControl = (BackgroundJobControl)typeof(BackgroundJobRuntime)
                    .GetField("_jobs", PrivateInstance)!.GetValue(runtime)!;
                var controlSync = typeof(BackgroundJobControl).GetField("_sync", PrivateInstance)!.GetValue(actualControl)!;
                lock (controlSync)
                {
                    var executions = (Dictionary<Guid, BackgroundJobExecutionControl>)typeof(BackgroundJobControl)
                        .GetField("_executions", PrivateInstance)!.GetValue(actualControl)!;
                    if (capturedExecution is null) executions.TryGetValue(job.Value, out capturedExecution);
                }
                var actualAdmission = capturedExecution is null ? null : CapacityNoStartAdmissionTask(capturedExecution);
                var admissionResult = actualAdmission?.IsCompletedSuccessfully == true ? await actualAdmission : null;
                var admissionFacts = admissionResult is not null
                    ? $"{admissionResult.Outcome};dirty={admissionResult.CleanupFailed};receipt={admissionResult.Receipt is not null};validated={admissionResult.ValidatedEnvelope is not null}"
                    : actualAdmission?.Status.ToString() ?? "none";
                var jobFacts = "unavailable";
                try
                {
                    using var diagnostic = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await using var observed = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString, diagnostic.Token);
                    await using var observedTransaction = await observed.BeginTransactionAsync(diagnostic.Token);
                    await using (var announce = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)", observed, observedTransaction)
                    { CommandTimeout = 5 })
                    {
                        announce.Parameters.AddWithValue("tenant", SchemaFixture.TenantA.ToString());
                        await announce.ExecuteNonQueryAsync(diagnostic.Token);
                    }
                    await using var query = new NpgsqlCommand("""
                        SELECT current_user, phase, normal_attempts, ownership_kind,
                            (SELECT count(*) FROM background_job_attempts WHERE job_id=j.id)
                        FROM background_jobs j WHERE id=@job
                        """, observed, observedTransaction) { CommandTimeout = 5 };
                    query.Parameters.AddWithValue("job", job.Value);
                    await using var observedRow = await query.ExecuteReaderAsync(diagnostic.Token);
                    if (await observedRow.ReadAsync(diagnostic.Token))
                        jobFacts = $"role={observedRow.GetString(0)};phase={observedRow.GetString(1)};normal={observedRow.GetInt32(2)};kind={(observedRow.IsDBNull(3) ? "none" : observedRow.GetString(3))};attempts={observedRow.GetInt64(4)}";
                }
                catch (Exception diagnosticFailure) when (diagnosticFailure.GetType() != typeof(OutOfMemoryException)
                    && diagnosticFailure.GetType() != typeof(StackOverflowException))
                {
                    jobFacts = $"readFailure={diagnosticFailure.GetType().FullName};sqlstate={(diagnosticFailure as PostgresException)?.SqlState ?? "none"}";
                }
                throw new InvalidOperationException($"No-start admission premise did not enter disposal. proxyConnections={proxy.AcceptedConnections};commits={proxy.CompletedCommits};dropped={proxy.DroppedCommitResponses};"
                    + $"command={state.Command is not null};capturedExecution={state.Execution is not null};actualExecution={capturedExecution is not null};"
                    + $"readUnit={observation.ReadUnit is not null};startUnit={observation.StartUnit is not null};readDisposed={observation.ReadScopeDisposed};startDisposed={observation.StartScopeDisposed};"
                    + $"admission={admissionFacts};job={jobFacts};runtime={runtime.Snapshot}", failure);
            }
            proxy.CompletedCommits.Should().Be(2);
            var execution = state.Execution!;
            var command = state.Command!;
            var originalOwner = state.OriginalOwner!;
            var admission = CapacityNoStartAdmissionTask(execution);
            var initial = await ReadCapacityNoStartAsync(database, job);
            state.OriginalFacts = initial.Facts;
            state.OriginalGeneration = initial.Generation;
            initial.Phase.Should().Be("executing");
            initial.NormalAttempts.Should().Be(1);
            initial.Facts.Ordinal.Should().Be(1);
            initial.Facts.Deadline.Should().BeAfter(initial.Facts.AdmittedAt);
            initial.Facts.AdmissionToken.Should().Be(command.Claim.Token);
            AssertCapacityNoStartUnreleased(initial);
            originalOwner.CleanupConfirmed.Should().BeFalse("the actual original main scope is still held");
            admission.IsCompleted.Should().BeFalse();
            state.CheckpointEntered.Task.IsCompleted.Should().BeFalse();
            probe.Constructions.Should().BeEmpty();
            Fixture.RootConstructions.Should().Be(roots);

            // Keep the accepted one-second retry observable without allowing a
            // second business admission to obscure the original no-start proof.
            provider.Limit = 0;
            ConfirmedStartReceipt? published = null;
            if (loseAdmissionAck)
            {
                command.CommitStarted.Should().BeTrue();
                command.Receipt.Should().BeNull();
                execution.Identity.Should().BeNull();
                proxy.DroppedCommitResponses.Should().Be(1);
            }
            else
            {
                published = await execution.Publication.Completion.WaitAsync(TestWait);
                published.Should().NotBeNull();
                command.Receipt.Should().BeSameAs(published);
                var acknowledged = published!;
                acknowledged.SourceOwner.Should().BeSameAs(originalOwner);
                acknowledged.AttemptId.Value.Should().Be(initial.Facts.Attempt);
                acknowledged.AdmittedAt.Should().Be(initial.Facts.AdmittedAt);
                acknowledged.Deadline.Should().Be(initial.Facts.Deadline);
                execution.CloseExecution();
                await state.Callback.Entered.Task.WaitAsync(TestWait);
                execution.Token.IsCancellationRequested.Should().BeTrue();
                proxy.DroppedCommitResponses.Should().Be(0);
            }

            state.Disposal.Release.TrySetResult();
            var completed = await admission.WaitAsync(TestWait);
            completed.SourceOwner.Should().BeSameAs(originalOwner);
            completed.CleanupFailed.Should().BeFalse();
            completed.Outcome.Should().Be(loseAdmissionAck ? JobStartExecutionOutcome.Indeterminate : JobStartExecutionOutcome.Started);
            completed.Receipt.Should().BeSameAs(published);
            completed.ValidatedEnvelope.Should().BeSameAs(command.Envelope);
            (await execution.Publication.Completion.WaitAsync(TestWait)).Should().BeSameAs(published);
            await execution.WaitForPublicationAsync().WaitAsync(TestWait);
            if (loseAdmissionAck) execution.Identity.Should().BeNull("reconciliation never reconstructs executable authority");
            var originalIdentity = execution.Identity;
            observation.StartScopeDisposed.Should().BeTrue();
            observation.StartConnection!.State.Should().Be(ConnectionState.Closed);
            state.Disposal.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
            originalOwner.CleanupConfirmed.Should().BeTrue();
            state.VerifyClosedBinding = true;
            await state.Callback.Entered.Task.WaitAsync(TestWait);
            await EventuallyAsync(() => Task.FromResult((bool)typeof(BackgroundJobExecutionControl)
                .GetField("_observersStarted", PrivateInstance)!.GetValue(execution)!));
            execution.Gate.TryStartExecution().Should().BeFalse("native preparation atomically sealed the unused entry");
            typeof(BackgroundJobExecutionControl).GetField("_business", PrivateInstance)!.GetValue(execution)
                .Should().BeNull("the actual invocation never entered the business executor");
            var preparation = await CapacityNoStartPreparationPublication(execution).WaitAsync(TestWait);
            preparation.IsCompleted.Should().BeFalse("the actual private callback has not positively joined");
            state.Callback.Joined.Task.IsCompleted.Should().BeFalse();
            var early = await Task.WhenAny(state.CheckpointEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(300)));
            early.Should().NotBeSameAs(state.CheckpointEntered.Task);
            AssertCapacityNoStartUnreleased(await ReadCapacityNoStartAsync(database, job));
            NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
            runtime.Snapshot.Control!.Executions.Should().Be(1);

            await EventuallyAsync(async () => await VendorAsync(source, job) is { } vendor
                && await StateAsync(source, vendor) == "Succeeded");
            var generation = ActualGeneration(runtime, "transactional");
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, false));
            preparation.IsCompleted.Should().BeFalse("vendor success supplies no private-callback cleanup evidence");
            AssertCapacityNoStartUnreleased(await ReadCapacityNoStartAsync(database, job));

            state.Callback.Release.TrySetResult();
            await state.Callback.Joined.Task.WaitAsync(TestWait);
            var actualCleanup = await preparation.WaitAsync(TestWait);
            actualCleanup.Should().NotBeNull();
            actualCleanup!.SourceOwner.Should().BeSameAs(originalOwner);
            actualCleanup.Evidence.Should().Be(BackgroundJobCapacityCleanupEvidence.NeverStarted);
            actualCleanup.Receipt.Should().BeSameAs(published);
            actualCleanup.BusinessResult.Should().BeNull();
            if (loseAdmissionAck)
            {
                proxy.ReleaseHeldResponse();
                await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_outbox_admin",
                    controlName, CancellationToken.None);
                execution.Identity.Should().BeNull();
                NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
                state.CheckpointEntered.Task.IsCompleted.Should().BeFalse("the real locked admission probe must precede recovery");
                AssertCapacityNoStartUnreleased(await ReadCapacityNoStartAsync(database, job));
                await locked!.RollbackAsync();
                lockReleased = true;
                await locked!.DisposeAsync();
            }

            await state.CheckpointCompleted.Task.WaitAsync(TestWait);
            state.CheckpointEntered.Task.IsCompletedSuccessfully.Should().BeTrue();
            await EventuallyAsync(async () => (await ReadCapacityNoStartAsync(database, job)).NoStartAt is not null
                && runtime.Snapshot.Control!.Executions == 0);
            await EventuallyAsync(async () => (await ReadCapacityNoStartAsync(database, job)).Phase == "pending");
            var released = await ReadCapacityNoStartAsync(database, job);
            released.Facts.Should().Be(initial.Facts, "recovery preserves the immutable original attempt");
            released.NormalAttempts.Should().Be(1);
            released.Generation.Should().Be(initial.Generation + 1);
            released.TransactionalCleanupAt.Should().BeNull("an invocation that never entered is not a rolled-back handler");
            released.NoStartAt.Should().NotBeNull();
            released.ReleasedAt.Should().NotBeNull();
            released.ReleaseReason.Should().Be("never_started");
            var noStartAt = released.NoStartAt!.Value;
            var releasedAt = released.ReleasedAt!.Value;
            noStartAt.Should().BeOnOrAfter(initial.Facts.AdmittedAt);
            releasedAt.Should().BeOnOrAfter(noStartAt);
            releasedAt.Should().BeOnOrBefore(released.DbNow);
            execution.Identity.Should().BeSameAs(originalIdentity);
            typeof(BackgroundJobExecutionControl).GetField("_business", PrivateInstance)!.GetValue(execution).Should().BeNull();
            await EventuallyAsync(() => Task.FromResult(generation.WorkSnapshot.Running == 0 && generation.WorkSnapshot.Retained == 0));
            originalOwner.CleanupConfirmed.Should().BeTrue();
            var reopen = () => originalOwner.Enter();
            reopen.Should().Throw<InvalidOperationException>("only the final native join closes original issuance");
            calls.Should().Be(0);
            probe.Constructions.Should().BeEmpty();
            Fixture.RootConstructions.Should().Be(roots);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            verified = true;
        }
        finally
        {
            provider.Limit = 0;
            state.Disposal.Release.TrySetResult();
            state.Callback.Release.TrySetResult();
            proxy.ReleaseHeldResponse();
            state.Execution?.CloseExecution();
            if (locked is not null && !lockReleased)
            {
                await locked.RollbackAsync();
                lockReleased = true;
                await locked.DisposeAsync();
            }
            if (state.Execution is { } execution)
                await CapacityNoStartAdmissionTask(execution).WaitAsync(TestWait);
            await privateCallback.DisposeAsync().AsTask().WaitAsync(TestWait);
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TestWait);
            var stopped = false;
            try { await runtime.StopAsync(cleanup.Token); stopped = true; }
            catch (OperationCanceledException) when (!verified && cleanup.IsCancellationRequested) { }
            if (stopped)
            {
                await runtime.Completion.WaitAsync(TestWait);
                await DisposePinnedSourceAsync(source);
            }
        }
    }

    private sealed class CapacityNoStartState
    {
        internal AdmitBackgroundJobAttemptCommand? Command { get; set; }
        internal BackgroundJobExecutionControl? Execution { get; set; }
        internal JobApplicationSourceOwner? OriginalOwner { get; set; }
        internal IUnitOfWork? OriginalUnit { get; set; }
        internal CapacityNoStartFacts? OriginalFacts { get; set; }
        internal long OriginalGeneration { get; set; }
        internal bool VerifyClosedBinding { get; set; }
        internal FacadeCleanupSignals Disposal { get; } = new();
        internal FacadeCleanupSignals Callback { get; } = new();
        internal TaskCompletionSource CheckpointEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CheckpointCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CapacityNoStartAdmissionDisposal(CapacityNoStartState state, IUnitOfWork unit) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            unit.Should().BeSameAs(state.OriginalUnit);
            unit.Transaction.Should().BeNull("the actual admission COMMIT resolved before the scope cleanup hold");
            state.Disposal.Entered.TrySetResult();
            await state.Disposal.Release.Task;
            state.Disposal.Joined.TrySetResult();
        }
    }

    private sealed class CapacityNoStartCheckpointObserver(CapacityNoStartState state, IUnitOfWork unit)
        : IPipelineBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(RecordBackgroundJobCapacityCleanupCommand request,
            RequestHandlerDelegate<Result<None>> next, CancellationToken cancellationToken)
        {
            state.CheckpointEntered.TrySetResult();
            try
            {
                request.SourceOwner.Should().BeSameAs(state.OriginalOwner);
                JobApplicationSourceOwner.Current.Should().BeSameAs(state.OriginalOwner);
                request.Evidence.Should().Be(BackgroundJobCapacityCleanupEvidence.NeverStarted);
                unit.Should().NotBeSameAs(state.OriginalUnit);
                state.Callback.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
                state.Disposal.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
                AuxiliaryCleanupResources(request.SourceOwner).Should().BeNull();
                var original = state.OriginalFacts!;
                var recovery = request.Recovery;
                var admission = state.Command!;
                recovery.JobId.Should().Be(admission.Claim.JobId);
                recovery.TenantId.Should().Be(admission.Claim.TenantId);
                recovery.OrganizationId.Should().Be(admission.Claim.OrganizationId);
                recovery.DefinitionKey.Should().Be(admission.Envelope.DefinitionKey);
                recovery.Profile.Should().Be(admission.Envelope.Profile);
                recovery.Queue.Should().Be(admission.Envelope.Queue);
                recovery.CapacityGroup.Should().Be(BackgroundJobRuntimeFixture.CapacityGroup);
                recovery.AttemptId.Value.Should().Be(original.Attempt);
                recovery.AdmissionToken.Should().Be(original.AdmissionToken);
                recovery.NormalAttemptOrdinal.Should().Be(original.Ordinal);
                recovery.AdmittedAt.Should().Be(original.AdmittedAt);
                recovery.Deadline.Should().Be(original.Deadline);
                recovery.OriginalDueAt.Should().Be(original.OriginalDueAt);
                recovery.WakeGeneration.Should().Be(state.OriginalGeneration);
                recovery.RecoveryToken.Should().NotBe(original.AdmissionToken);
                recovery.OwnershipUntil.Should().BeAfter(recovery.DbNow);
                request.RequireOwner(unit);
                (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
                var current = (await BackgroundJobRuntimeRows.ReadInUnitAsync(unit, recovery.JobId))!;
                current.Phase.Should().Be("reconciling");
                current.OwnershipKind.Should().Be("recovery");
                current.OwnershipToken.Should().Be(recovery.RecoveryToken);
                current.CurrentAttempt.Should().Be(original.Attempt);
                request.RequireOwner(unit);
                var result = await next();
                result.IsSuccess.Should().BeTrue();
                state.CheckpointCompleted.TrySetResult();
                return result;
            }
            catch (Exception failure)
            {
                state.CheckpointCompleted.TrySetException(new InvalidOperationException(
                    $"No-start checkpoint observer failed: type={failure.GetType().FullName};sqlstate={(failure as PostgresException)?.SqlState ?? "none"};constraint={(failure as PostgresException)?.ConstraintName ?? "none"};stack={failure.StackTrace}"));
                throw;
            }
        }
    }

    private sealed record CapacityNoStartFacts(Guid Attempt, Guid AdmissionToken, int Ordinal,
        DateTimeOffset AdmittedAt, DateTimeOffset Deadline, DateTimeOffset OriginalDueAt);

    private sealed record CapacityNoStartRow(CapacityNoStartFacts Facts, int NormalAttempts, string Phase, long Generation,
        DateTimeOffset? NoStartAt, DateTimeOffset? TransactionalCleanupAt, DateTimeOffset? ReleasedAt, string? ReleaseReason,
        DateTimeOffset DbNow);

    private static void AssertCapacityNoStartUnreleased(CapacityNoStartRow row)
    {
        row.NoStartAt.Should().BeNull();
        row.TransactionalCleanupAt.Should().BeNull();
        row.ReleasedAt.Should().BeNull();
        row.ReleaseReason.Should().BeNull();
    }

    private static Task<JobStartExecutionResult> CapacityNoStartAdmissionTask(BackgroundJobExecutionControl execution) =>
        (Task<JobStartExecutionResult>)typeof(BackgroundJobExecutionControl).GetField("_admission", PrivateInstance)!.GetValue(execution)!;

    private static Task<Task<BackgroundJobActualCleanup?>> CapacityNoStartPreparationPublication(BackgroundJobExecutionControl execution)
    {
        var invocation = (HangfireGenerationWork.Invocation)typeof(BackgroundJobExecutionControl)
            .GetField("_work", PrivateInstance)!.GetValue(execution)!;
        var published = (TaskCompletionSource<Task<BackgroundJobActualCleanup?>>)typeof(HangfireGenerationWork.Invocation)
            .GetField("_cleanupPublished", PrivateInstance)!.GetValue(invocation)!;
        return published.Task;
    }

    private static async Task<CapacityNoStartRow> ReadCapacityNoStartAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand("""
            SELECT a.id, a.admission_token, a.ordinal, a.admitted_at, a.deadline, j.original_due_at,
                j.normal_attempts, j.phase, j.wake_generation, a.no_start_at, a.transactional_cleanup_at,
                r.released_at, r.release_reason, clock_timestamp(),
                j.processed_at, a.plan_fingerprint, a.operation_id, a.execute_intent_at,
                a.outcome, a.outcome_kind, a.outcome_at, a.quiescence_id, a.quiescence_kind, a.quiescent_at,
                (SELECT count(*) FROM background_job_attempts WHERE job_id=j.id),
                (SELECT count(*) FROM background_job_reservations WHERE job_id=j.id)
            FROM background_jobs j JOIN background_job_attempts a ON a.tenant_id=j.tenant_id AND a.job_id=j.id
            JOIN background_job_reservations r ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
            WHERE j.id=@job AND j.capacity_group=@group AND r.capacity_group=j.capacity_group
                AND a.stage='normal' AND a.ordinal=1
            """, connection, transaction);
        command.Parameters.AddWithValue("job", job.Value);
        command.Parameters.AddWithValue("group", BackgroundJobRuntimeFixture.CapacityGroup);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        var facts = new CapacityNoStartFacts(row.GetGuid(0), row.GetGuid(1), row.GetInt32(2),
            row.GetFieldValue<DateTimeOffset>(3), row.GetFieldValue<DateTimeOffset>(4), row.GetFieldValue<DateTimeOffset>(5));
        for (var index = 14; index < 24; index++)
            row.IsDBNull(index).Should().BeTrue("never-started cleanup creates no business or external-effect evidence");
        row.GetInt64(24).Should().Be(1, "recovery never spends a second normal attempt");
        row.GetInt64(25).Should().Be(1, "the exact original reservation is retained as history");
        var observed = new CapacityNoStartRow(facts, row.GetInt32(6), row.GetString(7), row.GetInt64(8),
            row.IsDBNull(9) ? null : row.GetFieldValue<DateTimeOffset>(9),
            row.IsDBNull(10) ? null : row.GetFieldValue<DateTimeOffset>(10),
            row.IsDBNull(11) ? null : row.GetFieldValue<DateTimeOffset>(11),
            row.IsDBNull(12) ? null : row.GetString(12), row.GetFieldValue<DateTimeOffset>(13));
        (await row.ReadAsync()).Should().BeFalse();
        return observed;
    }
}

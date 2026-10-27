using System.Data;
using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Native_NoPlan_keeps_a_cleaned_permanent_Prepare_decision_after_wait_closure_but_not_host_stop_or_owned_cancellation(
        bool stopAfterCleanup, bool cancelInsideHandler)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var probe = new NativeNoPlanPrepareProbe();
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        var time = new FinalizerPolicyJoinClock();
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(3),
            ProbeTimeout = TimeSpan.FromSeconds(3),
            ProgressTimeout = TimeSpan.FromSeconds(30),
            ControlPoll = TimeSpan.FromMilliseconds(50),
            TransactionTimeout = TimeSpan.FromSeconds(60),
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
        };
        bounds.Validate();
        await using var host = Fixture.Host(database, provider.Settings, construction, services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(probe);
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(NativeNoPlanPolicyObservation<,>));
            var request = Fixture.RequestType("Prepare");
            var plan = Fixture.Registry.Resolve(BackgroundJobPositiveSdkFixture.Key).Definition!.ProfileRegistration.PlanType!;
            var contract = typeof(IRequestHandler<,>).MakeGenericType(request, typeof(Result<>).MakeGenericType(plan));
            services.RemoveAll(contract);
            services.AddTransient(contract, typeof(NativeNoPlanPermanentPrepare<,>).MakeGenericType(request, plan));
        }, applicationPoolSize: 32);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds, time);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(Guid.CreateVersion7(), prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        BackgroundJobExecutionControl? execution = null;
        Guid originalAttempt = default;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            await probe.Written.Task.WaitAsync(TimeSpan.FromSeconds(45));
            execution = NativeFinalizerControl(runtime, job); // Existing lookup returns the actual job control, independent of stage.
            execution.Identity!.Stage.Should().Be(BackgroundJobExecutionStage.Prepare);
            execution.RunGate.IsClosed.Should().BeFalse();
            var business = (Task<JobBusinessExecutionResult>)NativeSdkField(execution, "_business");
            business.IsCompleted.Should().BeFalse("the actual Prepare handler is held after its nested business write");
            originalAttempt = execution.Identity.AttemptId.Value;
            var initial = await NativeNoPlanPolicyFactsAsync(database, job);
            initial.Phase.Should().Be("preparing");
            initial.Attempt.Should().Be(originalAttempt);
            initial.NoStarts.Should().Be(0);
            initial.Released.Should().Be(0);
            initial.NoPlanOrEffect.Should().BeTrue();
            time.Arm(execution);
            if (cancelInsideHandler) execution.CloseExecution();
            else probe.AllowReturn.TrySetResult();

            (await time.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(45))).Should().BeSameAs(execution);
            var result = await business.WaitAsync(TimeSpan.FromSeconds(5));
            result.ExecutionIssued.Should().BeTrue();
            result.KnownRollback.Should().BeTrue();
            result.CommitAcknowledged.Should().BeFalse();
            result.DurableOutcomeUncertain.Should().BeFalse();
            result.CleanupFailed.Should().BeFalse();
            result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            result.Receipt.Stage.Should().Be(BackgroundJobExecutionStage.Prepare);
            result.Receipt.AttemptId.Value.Should().Be(originalAttempt);
            result.Receipt.NormalAttemptOrdinal.Should().Be(1);
            result.Receipt.FinalizeAttemptOrdinal.Should().Be(0);
            var attempt = probe.Attempt ?? throw new InvalidOperationException("The actual Prepare owner was not captured.");
            attempt.Source.Should().BeSameAs(result.SourceOwner);
            attempt.Unit.HasActiveTransaction.Should().BeFalse();
            attempt.Connection.State.Should().Be(ConnectionState.Closed);
            Action disposedUnit = () => _ = attempt.Unit.Connection;
            disposedUnit.Should().Throw<ObjectDisposedException>();
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(0,
                "the real nested Prepare write rolled back before observer cleanup is released");
            NativeNoPlanPolicyStatus(execution).Should().BeNull();
            if (cancelInsideHandler)
            {
                execution.Token.IsCancellationRequested.Should().BeTrue();
                result.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.RetainOwnership);
                result.Decision.TerminalReason.Should().BeNull();
                runtime.RequestStop();
            }
            else
            {
                result.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.Terminal);
                result.Decision.TerminalReason.Should().Be(BackgroundJobTerminalReason.PermanentFailure);
                result.Decision.ErrorCode.Should().Be("permanent_failure");
                execution.RunGate.IsClosed.Should().BeFalse("only the already-cleaned decision is being interrupted");
                if (stopAfterCleanup) runtime.RequestStop();
                else execution.CloseExecution();
            }
            execution.RunGate.IsClosed.Should().BeTrue();
            time.Release.TrySetResult();
            if (!stopAfterCleanup && !cancelInsideHandler)
            {
                using var statusWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (NativeNoPlanPolicyStatus(execution) is null) await Task.Delay(20, statusWait.Token);
                (await NativeNoPlanPolicyStatus(execution)!.WaitAsync(statusWait.Token)).Should().BeTrue();
                execution.RunGate.IsClosed.Should().BeTrue();
                NativeNoPlanPolicyFacts cleaned;
                do
                {
                    cleaned = await NativeNoPlanPolicyFactsAsync(database, job);
                    if (cleaned.NoStarts == 1 && cleaned.Released == 1) break;
                    await Task.Delay(20, statusWait.Token);
                } while (true);
                cleaned.Reason.Should().Be("permanent_failure");
                cleaned.Dead.Should().BeNull();
                cleaned.Until.Should().NotBeNull();
                cleaned.DbNow.Should().BeBefore(cleaned.Until!.Value,
                    "the actual app NoStart leaves its real lease; no fixture expiry is used after startup");
                (await FinalizerPolicyTerminalAsync(database, job)).Audits.Should().Be(0);
                using var terminalWait = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                NativeNoPlanPolicyFacts terminal;
                do
                {
                    terminal = await NativeNoPlanPolicyFactsAsync(database, job);
                    if (terminal.Dead is not null) break;
                    await Task.Delay(20, terminalWait.Token);
                } while (true);
                terminal.Dead!.Value.Should().BeOnOrAfter(cleaned.Until.Value);
                var audit = await FinalizerPolicyTerminalAsync(database, job);
                audit.Dead.Should().BeTrue();
                audit.Reason.Should().Be("permanent_failure");
                audit.Audits.Should().Be(1);
                audit.AuditReason.Should().Be("permanent_failure");
            }
        }
        finally
        {
            probe.AllowReturn.TrySetResult();
            time.Release.TrySetResult();
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control!.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control.Executions.Should().Be(0);
        runtime.Snapshot.Control.PendingOutcomes.Should().Be(0);
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
        probe.Prepares.Should().Be(1);
        probe.Finalizers.Should().Be(0);
        probe.TerminalNoStarts.Should().Be(stopAfterCleanup || cancelInsideHandler ? 0 : 1);
        construction.Calls.Should().Be(0, "the original real adapter remains registered but is never resolved");
        await using (var external = new NpgsqlConnection(provider.Settings.ConnectionString))
        {
            await external.OpenAsync();
            await using var effects = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM provider_operations)+(SELECT count(*) FROM provider_attempts)
                    +(SELECT count(*) FROM provider_calls)+(SELECT count(*) FROM provider_effects)
                """, external) { CommandTimeout = 5 };
            ((long)(await effects.ExecuteScalarAsync())!).Should().Be(0);
        }
        var after = await NativeNoPlanPolicyFactsAsync(database, job);
        after.Attempt.Should().Be(originalAttempt);
        after.NormalAttempts.Should().Be(1);
        after.FinalizeAttempts.Should().Be(0);
        after.Ordinals.Should().Equal(1);
        after.Reservations.Should().Be(1);
        after.NoPlanOrEffect.Should().BeTrue();
        after.CleanLedger.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(0);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
        if (stopAfterCleanup || cancelInsideHandler)
        {
            NativeNoPlanPolicyStatus(execution!).Should().BeNull("shutdown cannot issue a later status task");
            after.Phase.Should().Be("preparing");
            after.Reason.Should().BeNull();
            after.Dead.Should().BeNull();
            after.NoStarts.Should().Be(0);
            after.Released.Should().Be(0);
            var audit = await FinalizerPolicyTerminalAsync(database, job);
            audit.Audits.Should().Be(0);
            audit.AuditReason.Should().BeNull();
        }
        else
        {
            after.Phase.Should().Be("terminal_pending");
            after.Reason.Should().Be("permanent_failure");
            after.NoStarts.Should().Be(1);
            after.Released.Should().Be(1);
        }
    }

    private static Task<bool>? NativeNoPlanPolicyStatus(BackgroundJobExecutionControl execution) =>
        (Task<bool>?)typeof(BackgroundJobExecutionControl).GetField("_noPlanTerminalDisposition",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(execution);

    private sealed record NativeNoPlanPolicyFacts(string Phase, string? Reason, Guid Attempt, int NormalAttempts,
        int FinalizeAttempts, DateTimeOffset? Until, DateTimeOffset? Dead, DateTimeOffset DbNow,
        bool NoPlanOrEffect, int[] Ordinals, long NoStarts, long Reservations, long Released, bool CleanLedger);

    private static async Task<NativeNoPlanPolicyFacts> NativeNoPlanPolicyFactsAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.terminal_reason,j.current_attempt_id,j.normal_attempts,j.finalize_attempts,
                j.ownership_until,j.dead_lettered_at,clock_timestamp(),
                j.plan IS NULL AND j.plan_fingerprint IS NULL AND j.operation_id IS NULL AND j.prepared_at IS NULL
                    AND j.final_outcome_attempt_id IS NULL AND j.finalization_status IS NULL AND j.finalization_reason IS NULL
                    AND NOT j.business_finalization_incomplete AND j.processed_at IS NULL,
                ARRAY(SELECT a.ordinal FROM background_job_attempts a WHERE a.job_id=j.id ORDER BY a.ordinal),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.no_start_at IS NOT NULL),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id
                    AND r.released_at IS NOT NULL AND r.release_reason='never_started'),
                NOT EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.job_id=j.id AND
                    (a.stage<>'normal' OR a.plan_fingerprint IS NOT NULL OR a.operation_id IS NOT NULL
                    OR a.execute_intent_at IS NOT NULL OR a.outcome IS NOT NULL OR a.outcome_kind IS NOT NULL
                    OR a.outcome_at IS NOT NULL OR a.failure_policy IS NOT NULL OR a.quiescence_id IS NOT NULL
                    OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                    OR a.final_outcome_attempt_id IS NOT NULL OR a.transactional_cleanup_at IS NOT NULL))
            FROM background_jobs j WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetGuid(2), row.GetInt32(3), row.GetInt32(4),
            row.IsDBNull(5) ? null : row.GetFieldValue<DateTimeOffset>(5), row.IsDBNull(6) ? null : row.GetFieldValue<DateTimeOffset>(6),
            row.GetFieldValue<DateTimeOffset>(7), row.GetBoolean(8), row.GetFieldValue<int[]>(9), row.GetInt64(10),
            row.GetInt64(11), row.GetInt64(12), row.GetBoolean(13));
    }

    private sealed record NativeNoPlanPrepareAttempt(JobApplicationSourceOwner Source, IUnitOfWork Unit, DbConnection Connection);
    private sealed class NativeNoPlanPrepareProbe
    {
        internal TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal NativeNoPlanPrepareAttempt? Attempt;
        internal int Prepares, Finalizers, TerminalNoStarts;
    }
    private sealed class NativeNoPlanPolicyObservation<TRequest, TResponse>(NativeNoPlanPrepareProbe probe)
        : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (typeof(TRequest) == Fixture.RequestType("Finalize")) Interlocked.Increment(ref probe.Finalizers);
            if (request is RecordBackgroundJobOutcomeCommand { IsTerminalNoStart: true }) Interlocked.Increment(ref probe.TerminalNoStarts);
            return await next();
        }
    }
    private sealed class NativeNoPlanPermanentPrepare<TRequest, TPlan>(ISender sender, IUnitOfWork unit, NativeNoPlanPrepareProbe probe)
        : IRequestHandler<TRequest, Result<TPlan>> where TRequest : notnull, IRequest<Result<TPlan>> where TPlan : IBackgroundJobPlan
    {
        public async Task<Result<TPlan>> Handle(TRequest request, CancellationToken cancellationToken)
        {
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction!);
            if (Interlocked.Increment(ref probe.Prepares) != 1) throw new InvalidOperationException("Unexpected second Prepare root.");
            var source = JobApplicationSourceOwner.Current ?? throw new InvalidOperationException("Actual Prepare source missing.");
            probe.Attempt = new(source, unit, unit.Connection);
            var parameters = typeof(TRequest).GetProperty("Parameters")!.GetValue(request)!;
            var prepared = (OrganizationId)parameters.GetType().GetProperty("PreparedOrganizationId")!.GetValue(parameters)!;
            var written = await sender.Send(Fixture.OrganizationRequest(prepared,
                "prepared-" + prepared.Value.ToString("N"), "Prepare policy window"), cancellationToken);
            if (written.IsFailure) throw new InvalidOperationException("Actual nested Prepare write failed.");
            probe.Written.TrySetResult();
            await probe.AllowReturn.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            return Result<TPlan>.Fail(new Error(new LocalizedMessage("lockey_validation_failed")));
        }
    }
}

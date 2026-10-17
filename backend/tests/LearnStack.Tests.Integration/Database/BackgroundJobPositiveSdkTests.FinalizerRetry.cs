using System.Data;
using System.Data.Common;
using FluentAssertions;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_Finalize_retry_rolls_back_the_first_nested_write_and_uses_fresh_authority_without_reexecuting_provider(
        bool throwConcurrency)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await provider.SetModeAsync(resource, "succeeded");
        await InstallTransactionObserverAsync(database, prepared, finalized);
        var probe = new NativeFinalizerRetryProbe(throwConcurrency);
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        var bounds = new BackgroundJobRuntimeOptions
        {
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
            TransactionTimeout = TimeSpan.FromSeconds(60),
        };
        await using var host = Fixture.Host(database, provider.Settings, construction, services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(probe);
            var request = Fixture.RequestType("Finalize");
            var contract = typeof(IRequestHandler<,>).MakeGenericType(request, typeof(Result<None>));
            services.RemoveAll(contract);
            services.AddTransient(contract, typeof(NativeFinalizerRetryHandler<>).MakeGenericType(request));
        }, applicationPoolSize: 24);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            await probe.FirstWriteReady.Task.WaitAsync(TimeSpan.FromSeconds(50));
            var firstControl = NativeFinalizerControl(runtime, job);
            var firstTask = (Task<JobBusinessExecutionResult>)NativeSdkField(firstControl, "_business");
            firstTask.IsCompleted.Should().BeFalse("the real first root is held after its nested business write");
            var initial = await ReadNativeFinalizerFactsAsync(database, job);
            initial.Phase.Should().Be("finalizing");
            initial.NormalAttempts.Should().Be(1);
            initial.FinalizeAttempts.Should().Be(1);
            initial.NormalRows.Should().Be(1);
            initial.FinalizerIds.Should().ContainSingle().Which.Should().Be(initial.CurrentAttempt);
            initial.Selection.OutcomeKind.Should().Be("succeeded");
            initial.Selection.Status.Should().Be("succeeded");
            initial.Selection.Reason.Should().Be("none");
            initial.Reservations.Should().Be(1);
            initial.ReleasedReservations.Should().Be(1);
            (await provider.CountsAsync(initial.Selection.Operation)).Should().Be((1L, 0L, 1L));
            construction.Calls.Should().Be(1);
            construction.ExternalDenial.Should().BeTrue();

            probe.AllowFirstReturn.TrySetResult();
            await probe.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(50));
            var secondControl = NativeFinalizerControl(runtime, job);
            secondControl.Should().NotBeSameAs(firstControl);
            var secondTask = (Task<JobBusinessExecutionResult>)NativeSdkField(secondControl, "_business");
            secondTask.IsCompleted.Should().BeFalse("the fresh second root is held before its business write");
            var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
            first.ExecutionIssued.Should().BeTrue();
            first.CommitAcknowledged.Should().BeFalse();
            first.KnownRollback.Should().BeTrue();
            first.DurableOutcomeUncertain.Should().BeFalse();
            first.CleanupFailed.Should().BeFalse();
            first.SourceOwner.CleanupConfirmed.Should().BeTrue();
            first.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.Retry);
            first.Decision.ErrorCode.Should().Be("concurrency_conflict");
            first.Receipt.FinalizeAttemptOrdinal.Should().Be(1);
            first.Receipt.NormalAttemptOrdinal.Should().Be(1);
            first.Receipt.SelectedNormalAttemptId!.Value.Value.Should().Be(initial.Selection.SelectedNormalAttempt);
            first.FinalizationRead!.CleanupFailed.Should().BeFalse();
            first.FinalizationRead.CommitAcknowledged.Should().BeTrue();

            var held = await ReadNativeFinalizerFactsAsync(database, job);
            held.Phase.Should().Be("finalizing");
            held.NormalAttempts.Should().Be(1);
            held.FinalizeAttempts.Should().Be(2);
            held.NormalRows.Should().Be(1);
            held.FinalizerIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            held.FinalizerIds[0].Should().Be(initial.CurrentAttempt);
            held.FinalizerIds[1].Should().Be(held.CurrentAttempt);
            held.CurrentAttempt.Should().NotBe(initial.CurrentAttempt);
            held.Selection.Should().Be(initial.Selection,
                "finalizer retry cannot replace the selected normal outcome, plan, operation or release evidence");
            held.Reservations.Should().Be(1);
            held.ReleasedReservations.Should().Be(1);
            held.BusinessFinalizationIncomplete.Should().BeFalse();
            held.TerminalReason.Should().BeNull();
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0,
                "the first successful nested write rolled back and the second root has not written yet");
            (await provider.CountsAsync(initial.Selection.Operation)).Should().Be((1L, 0L, 1L));
            construction.Calls.Should().Be(1, "retrying Finalize cannot activate Execute or Reconcile");
            var attempts = probe.Snapshot();
            attempts.Should().HaveCount(2);
            attempts[0].Source.Should().BeSameAs(first.SourceOwner);
            attempts[1].Source.Should().NotBeSameAs(attempts[0].Source);
            attempts[1].Unit.Should().NotBeSameAs(attempts[0].Unit);
            attempts[1].Connection.Should().NotBeSameAs(attempts[0].Connection);
            attempts[1].TransactionId.Should().NotBe(attempts[0].TransactionId,
                "separate database transactions can reuse Npgsql's pooled transaction object");
            attempts[1].Input.Should().Be(attempts[0].Input);
            attempts[0].Input.Status.Should().Be(BackgroundJobFinalizationStatus.Succeeded);
            attempts[0].Input.Reason.Should().Be(BackgroundJobFinalizationReason.None);
            attempts[0].Input.Plan.Should().Be(initial.Selection.Plan);
            attempts[0].Input.Outcome.Should().Be(initial.Selection.Outcome);
            AssertNativeFinalizerCleaned(attempts[0]);
            attempts[1].Unit.HasActiveTransaction.Should().BeTrue();

            probe.AllowSecondWrite.TrySetResult();
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase != "completed"
                || runtime.Snapshot.Control!.Executions != 0)
                await Task.Delay(20, completion.Token);
            var second = await secondTask.WaitAsync(completion.Token);
            second.ExecutionIssued.Should().BeTrue();
            second.CommitAcknowledged.Should().BeTrue();
            second.CleanupFailed.Should().BeFalse();
            second.SourceOwner.Should().BeSameAs(attempts[1].Source);
            second.FinalizationRead!.CommitAcknowledged.Should().BeTrue();
            second.FinalizationRead.CleanupFailed.Should().BeFalse();
            first.Receipt.AttemptId.Value.Should().Be(initial.CurrentAttempt);
            second.Receipt.AttemptId.Value.Should().Be(held.CurrentAttempt);
            second.Receipt.SelectedNormalAttemptId.Should().Be(first.Receipt.SelectedNormalAttemptId);
            second.Receipt.FinalizeAttemptOrdinal.Should().Be(2);
            second.Receipt.NormalAttemptOrdinal.Should().Be(1);
            AssertNativeFinalizerCleaned(attempts[1]);
            probe.Snapshot().Should().HaveCount(2);

            var completed = await ReadNativeFinalizerFactsAsync(database, job);
            completed.Phase.Should().Be("completed");
            completed.NormalAttempts.Should().Be(1);
            completed.FinalizeAttempts.Should().Be(2);
            completed.NormalRows.Should().Be(1);
            completed.FinalizerIds.Should().Equal(held.FinalizerIds);
            completed.Selection.Should().Be(initial.Selection);
            completed.Reservations.Should().Be(1);
            completed.ReleasedReservations.Should().Be(1);
            completed.BusinessFinalizationIncomplete.Should().BeFalse();
            completed.TerminalReason.Should().BeNull();
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
            (await provider.CountsAsync(initial.Selection.Operation)).Should().Be((1L, 0L, 1L));
            construction.Calls.Should().Be(1);
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await AssertObservedTransactionsAsync(app, transaction, failure: false);
        }
        finally
        {
            probe.AllowFirstReturn.TrySetResult();
            probe.AllowSecondWrite.TrySetResult();
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control!.Executions.Should().Be(0);
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
    }

    private static BackgroundJobExecutionControl NativeFinalizerControl(BackgroundJobRuntime runtime, BackgroundJobId job)
    {
        var controller = (BackgroundJobControl)NativeSdkField(runtime, "_jobs");
        lock (NativeSdkField(controller, "_sync"))
            return ((Dictionary<Guid, BackgroundJobExecutionControl>)NativeSdkField(controller, "_executions"))[job.Value];
    }

    private static void AssertNativeFinalizerCleaned(NativeFinalizerAttempt attempt)
    {
        attempt.Source.CleanupConfirmed.Should().BeTrue();
        attempt.Unit.HasActiveTransaction.Should().BeFalse();
        attempt.Connection.State.Should().Be(ConnectionState.Closed);
        Action accessDisposedUnit = () => _ = attempt.Unit.Connection;
        accessDisposedUnit.Should().Throw<ObjectDisposedException>("the actual scoped unit has been disposed");
    }

    private sealed record NativeFinalizerInput(string Plan, string Outcome,
        BackgroundJobFinalizationStatus Status, BackgroundJobFinalizationReason Reason);
    private sealed record NativeFinalizerAttempt(JobApplicationSourceOwner Source, IUnitOfWork Unit,
        DbConnection Connection, string TransactionId, NativeFinalizerInput Input);

    private sealed class NativeFinalizerRetryProbe(bool throwConcurrency)
    {
        private readonly object _sync = new();
        private readonly List<NativeFinalizerAttempt> _attempts = [];
        internal bool ThrowConcurrency { get; } = throwConcurrency;
        internal TaskCompletionSource FirstWriteReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowFirstReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowSecondWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Observe(NativeFinalizerAttempt attempt)
        {
            lock (_sync) { _attempts.Add(attempt); return _attempts.Count; }
        }
        internal NativeFinalizerAttempt[] Snapshot() { lock (_sync) return _attempts.ToArray(); }
    }

    // Closed over the fixture's real registered Finalize request. Reflection
    // reads its DTOs only; the request, receipt, source and authority are production-owned.
    private sealed class NativeFinalizerRetryHandler<TRequest>(ISender sender, IUnitOfWork unit, NativeFinalizerRetryProbe probe)
        : IRequestHandler<TRequest, Result<None>> where TRequest : notnull, IRequest<Result<None>>
    {
        public async Task<Result<None>> Handle(TRequest request, CancellationToken cancellationToken)
        {
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction!);
            var source = JobApplicationSourceOwner.Current
                ?? throw new InvalidOperationException("The actual finalizer has no application source.");
            var parameters = typeof(TRequest).GetProperty("Parameters")!.GetValue(request)!;
            var finalization = typeof(TRequest).GetProperty("Finalization")!.GetValue(request)!;
            var plan = finalization.GetType().GetProperty("Plan")!.GetValue(finalization)!;
            var outcome = finalization.GetType().GetProperty("Outcome")!.GetValue(finalization)!;
            var resource = (Guid)parameters.GetType().GetProperty("ResourceId")!.GetValue(parameters)!;
            var finalized = (OrganizationId)parameters.GetType().GetProperty("FinalizedOrganizationId")!.GetValue(parameters)!;
            if ((Guid)plan.GetType().GetProperty("ResourceId")!.GetValue(plan)! != resource
                || (OrganizationId)plan.GetType().GetProperty("FinalizedOrganizationId")!.GetValue(plan)! != finalized
                || (Guid)outcome.GetType().GetProperty("ResourceId")!.GetValue(outcome)! != resource)
                throw new InvalidOperationException("Finalizer DTO identity changed.");
            var input = new NativeFinalizerInput(
                BackgroundJobPayloadContract.Serialize(plan, plan.GetType(), BackgroundJobPayloadKind.Plan),
                BackgroundJobPayloadContract.Serialize(outcome, outcome.GetType(), BackgroundJobPayloadKind.Outcome),
                (BackgroundJobFinalizationStatus)finalization.GetType().GetProperty("Status")!.GetValue(finalization)!,
                (BackgroundJobFinalizationReason)finalization.GetType().GetProperty("Reason")!.GetValue(finalization)!);
            await using var transactionId = unit.Connection.CreateCommand();
            transactionId.Transaction = unit.Transaction;
            transactionId.CommandText = "SELECT pg_current_xact_id()::text";
            transactionId.CommandTimeout = 5;
            var actualTransaction = (string)(await transactionId.ExecuteScalarAsync(cancellationToken))!;
            var ordinal = probe.Observe(new(source, unit, unit.Connection, actualTransaction, input));
            if (ordinal is < 1 or > 2) throw new InvalidOperationException("Unexpected extra Finalize entry.");
            if (ordinal == 2)
            {
                probe.SecondEntered.TrySetResult();
                await probe.AllowSecondWrite.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            }
            var written = await sender.Send(new CreateOrganizationCommand(finalized,
                "finalized-" + finalized.Value.ToString("N"), "Finalized provider " + input.Status), cancellationToken);
            if (written.IsFailure) throw new InvalidOperationException("The actual nested organization write was refused.");
            if (ordinal == 1)
            {
                probe.FirstWriteReady.TrySetResult();
                await probe.AllowFirstReturn.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
                if (probe.ThrowConcurrency) throw new AggregateConcurrencyException("Actual finalizer retry test conflict.");
                return Result<None>.Fail(new Error(new LocalizedMessage("lockey_concurrency_conflict")));
            }
            return Result<None>.Ok(None.Value);
        }
    }

    private sealed record NativeFinalizerSelection(Guid SelectedNormalAttempt, string Plan, string Fingerprint,
        Guid Operation, string Outcome, string OutcomeKind, DateTimeOffset OutcomeAt, Guid Quiescence,
        string QuiescenceKind, DateTimeOffset QuiescentAt, DateTimeOffset ReleasedAt, string ReleaseReason,
        string Status, string Reason);
    private sealed record NativeFinalizerFacts(string Phase, int NormalAttempts, int FinalizeAttempts,
        Guid CurrentAttempt, NativeFinalizerSelection Selection, long NormalRows, Guid[] FinalizerIds,
        long Reservations, long ReleasedReservations, bool BusinessFinalizationIncomplete, string? TerminalReason);

    private static async Task<NativeFinalizerFacts> ReadNativeFinalizerFactsAsync(DisposableSchemaDatabase database,
        BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.normal_attempts,j.finalize_attempts,j.current_attempt_id,
                j.final_outcome_attempt_id,j.plan,j.plan_fingerprint,j.operation_id,
                a.outcome,a.outcome_kind,a.outcome_at,a.quiescence_id,a.quiescence_kind,a.quiescent_at,
                r.released_at,r.release_reason,j.finalization_status,j.finalization_reason,
                (SELECT count(*) FROM background_job_attempts n WHERE n.job_id=j.id AND n.stage='normal'),
                ARRAY(SELECT f.id FROM background_job_attempts f WHERE f.job_id=j.id AND f.stage='finalize' ORDER BY f.ordinal),
                (SELECT count(*) FROM background_job_reservations x WHERE x.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations x WHERE x.job_id=j.id AND x.released_at IS NOT NULL),
                j.business_finalization_incomplete,j.terminal_reason
            FROM background_jobs j JOIN background_job_attempts a ON a.id=j.final_outcome_attempt_id
                AND a.job_id=j.id AND a.stage='normal'
            JOIN background_job_reservations r ON r.job_id=j.id AND r.attempt_id=a.id
            WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        var selection = new NativeFinalizerSelection(row.GetGuid(4), row.GetString(5), row.GetString(6), row.GetGuid(7),
            row.GetString(8), row.GetString(9), row.GetFieldValue<DateTimeOffset>(10), row.GetGuid(11), row.GetString(12),
            row.GetFieldValue<DateTimeOffset>(13), row.GetFieldValue<DateTimeOffset>(14), row.GetString(15),
            row.GetString(16), row.GetString(17));
        return new(row.GetString(0), row.GetInt32(1), row.GetInt32(2), row.GetGuid(3), selection,
            row.GetInt64(18), row.GetFieldValue<Guid[]>(19), row.GetInt64(20), row.GetInt64(21),
            row.GetBoolean(22), row.IsDBNull(23) ? null : row.GetString(23));
    }
}

using System.Reflection;
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Native_Finalize_preserves_a_cleaned_permanent_decision_across_observer_join_but_respects_stop_and_owned_cancellation(
        bool stopAfterCleanup, bool cancelInsideHandler)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await provider.SetModeAsync(resource, "succeeded");
        var probe = new NativeFinalizerRetryProbe(false);
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        var time = new FinalizerPolicyJoinClock();
        var bounds = new BackgroundJobRuntimeOptions
        {
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
            TransactionTimeout = TimeSpan.FromSeconds(60),
        };
        bounds.Validate();
        await using var host = Fixture.Host(database, provider.Settings, construction, services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(probe);
            var request = Fixture.RequestType("Finalize");
            var contract = typeof(IRequestHandler<,>).MakeGenericType(request, typeof(Result<None>));
            services.RemoveAll(contract);
            services.AddTransient(contract, typeof(NativeFinalizerPermanentHandler<>).MakeGenericType(request));
        }, applicationPoolSize: 24);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds, time);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        BackgroundJobExecutionControl? execution = null;
        NativeFinalizerFacts? initial = null;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            await probe.FirstWriteReady.Task.WaitAsync(TimeSpan.FromSeconds(50));
            execution = NativeFinalizerControl(runtime, job);
            execution.Identity!.Stage.Should().Be(BackgroundJobExecutionStage.Finalize);
            execution.RunGate.IsClosed.Should().BeFalse();
            var business = (Task<JobBusinessExecutionResult>)NativeSdkField(execution, "_business");
            business.IsCompleted.Should().BeFalse("the actual handler is held after its nested write");
            initial = await ReadNativeFinalizerFactsAsync(database, job);
            initial.Phase.Should().Be("finalizing");
            initial.FinalizeAttempts.Should().Be(1);
            initial.Selection.Status.Should().Be("succeeded");
            initial.Selection.Reason.Should().Be("none");
            time.Arm(execution);
            if (cancelInsideHandler)
            {
                execution.CloseExecution();
                // Leave the real handler blocked: its owned cancellation must
                // end WaitAsync and determine the actual classifier result.
            }
            else probe.AllowFirstReturn.TrySetResult();

            var disposing = await time.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            disposing.Should().BeSameAs(execution);
            var result = await business.WaitAsync(TimeSpan.FromSeconds(5));
            result.ExecutionIssued.Should().BeTrue();
            result.KnownRollback.Should().BeTrue();
            result.CommitAcknowledged.Should().BeFalse();
            result.DurableOutcomeUncertain.Should().BeFalse();
            result.CleanupFailed.Should().BeFalse();
            result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            result.Receipt.AttemptId.Value.Should().Be(initial.CurrentAttempt);
            result.Receipt.SelectedNormalAttemptId!.Value.Value.Should().Be(initial.Selection.SelectedNormalAttempt);
            result.Receipt.FinalizeAttemptOrdinal.Should().Be(1);
            result.FinalizationRead!.CommitAcknowledged.Should().BeTrue();
            result.FinalizationRead.CleanupFailed.Should().BeFalse();
            var attempt = probe.Snapshot().Should().ContainSingle().Which;
            attempt.Source.Should().BeSameAs(result.SourceOwner);
            AssertNativeFinalizerCleaned(attempt);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0,
                "the successful nested write rolled back before the observer barrier");
            FinalizerPolicyStatus(execution).Should().BeNull("no status task exists before the actual observer join returns");
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
                execution.RunGate.IsClosed.Should().BeFalse("closure is injected only after the real cleaned decision");
                if (stopAfterCleanup) runtime.RequestStop();
                else execution.CloseExecution();
            }
            execution.RunGate.IsClosed.Should().BeTrue();
            time.Release.TrySetResult();
            if (!stopAfterCleanup && !cancelInsideHandler)
            {
                using var statusWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (FinalizerPolicyStatus(execution) is null) await Task.Delay(20, statusWait.Token);
                var actualStatus = FinalizerPolicyStatus(execution)!;
                (await actualStatus.WaitAsync(statusWait.Token)).Should().BeTrue();
                execution.RunGate.IsClosed.Should().BeTrue("status cannot reopen ordinary invocation authority");
                var disposed = await ReadNativeFinalizerFactsAsync(database, job);
                disposed.TerminalReason.Should().Be("finalize_refused");
                disposed.Selection.Should().Be(initial.Selection);
                using var terminalWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!(await FinalizerPolicyTerminalAsync(database, job)).Dead)
                    await Task.Delay(20, terminalWait.Token);
                var terminal = await FinalizerPolicyTerminalAsync(database, job);
                terminal.Audits.Should().Be(1);
                terminal.Reason.Should().Be("finalize_refused");
                terminal.AuditReason.Should().Be("finalize_refused");
            }
        }
        finally
        {
            // Release before stop/join, including assertion failures and timeout.
            probe.AllowFirstReturn.TrySetResult();
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
        probe.Snapshot().Should().ContainSingle();
        var after = await ReadNativeFinalizerFactsAsync(database, job);
        after.NormalAttempts.Should().Be(1);
        after.FinalizeAttempts.Should().Be(1);
        after.NormalRows.Should().Be(1);
        after.FinalizerIds.Should().Equal(initial!.FinalizerIds);
        after.Selection.Should().Be(initial.Selection);
        after.Reservations.Should().Be(1);
        after.ReleasedReservations.Should().Be(1);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
        (await provider.CountsAsync(initial.Selection.Operation)).Should().Be((1L, 0L, 1L));
        construction.Calls.Should().Be(1);
        construction.ExternalDenial.Should().BeTrue();
        if (stopAfterCleanup || cancelInsideHandler)
        {
            FinalizerPolicyStatus(execution!).Should().BeNull("no status may be issued later during shutdown cleanup");
            after.Phase.Should().Be("finalizing");
            after.TerminalReason.Should().BeNull();
            after.BusinessFinalizationIncomplete.Should().BeFalse();
            var terminal = await FinalizerPolicyTerminalAsync(database, job);
            terminal.Dead.Should().BeFalse();
            terminal.Audits.Should().Be(0);
            terminal.AuditReason.Should().BeNull();
        }
        else after.BusinessFinalizationIncomplete.Should().BeTrue();
    }

    private static Task<bool>? FinalizerPolicyStatus(BackgroundJobExecutionControl execution) =>
        (Task<bool>?)typeof(BackgroundJobExecutionControl).GetField("_externalFinalizeDisposition",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(execution);

    private static async Task<(bool Dead, string? Reason, long Audits, string? AuditReason)> FinalizerPolicyTerminalAsync(
        DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT dead_lettered_at IS NOT NULL FROM background_jobs WHERE id=@id),
                (SELECT terminal_reason FROM background_jobs WHERE id=@id),
                count(*),min(metadata->>'reason') FROM audit_log
            WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@text
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        command.Parameters.AddWithValue("text", job.Value.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetBoolean(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetInt64(2),
            row.IsDBNull(3) ? null : row.GetString(3));
    }

    private sealed class FinalizerPolicyJoinClock : TimeProvider
    {
        private BackgroundJobExecutionControl? _target;
        private int _held;
        internal TaskCompletionSource<BackgroundJobExecutionControl> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm(BackgroundJobExecutionControl execution)
        {
            if (Interlocked.CompareExchange(ref _target, execution, null) is not null)
                throw new InvalidOperationException("One actual finalizer observer is selected.");
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var actual = System.CreateTimer(callback, state, dueTime, period);
            return state is BackgroundJobExecutionControl execution ? new FinalizerPolicyTimer(actual, execution, this) : actual;
        }
        private sealed class FinalizerPolicyTimer(ITimer actual, BackgroundJobExecutionControl execution,
            FinalizerPolicyJoinClock owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => actual.Change(dueTime, period);
            public void Dispose() => actual.Dispose();
            public async ValueTask DisposeAsync()
            {
                await actual.DisposeAsync();
                if (ReferenceEquals(Volatile.Read(ref owner._target), execution)
                    && Interlocked.CompareExchange(ref owner._held, 1, 0) == 0)
                {
                    owner.Disposed.TrySetResult(execution);
                    await owner.Release.Task;
                }
            }
        }
    }

    // The real registered ordinary Finalize root writes and then refuses. Its
    // cancellation-aware wait leaves classification to the actual executor.
    private sealed class NativeFinalizerPermanentHandler<TRequest>(ISender sender, IUnitOfWork unit, NativeFinalizerRetryProbe probe)
        : IRequestHandler<TRequest, Result<None>> where TRequest : notnull, IRequest<Result<None>>
    {
        public async Task<Result<None>> Handle(TRequest request, CancellationToken cancellationToken)
        {
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction!);
            var source = JobApplicationSourceOwner.Current ?? throw new InvalidOperationException("Actual Finalize source missing.");
            var parameters = typeof(TRequest).GetProperty("Parameters")!.GetValue(request)!;
            var finalization = typeof(TRequest).GetProperty("Finalization")!.GetValue(request)!;
            var plan = finalization.GetType().GetProperty("Plan")!.GetValue(finalization)!;
            var outcome = finalization.GetType().GetProperty("Outcome")!.GetValue(finalization)!;
            var finalized = (OrganizationId)parameters.GetType().GetProperty("FinalizedOrganizationId")!.GetValue(parameters)!;
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
            if (probe.Observe(new(source, unit, unit.Connection, actualTransaction, input)) != 1)
                throw new InvalidOperationException("Unexpected second Finalize root.");
            var written = await sender.Send(new CreateOrganizationCommand(finalized,
                "finalized-" + finalized.Value.ToString("N"), "Finalizer policy window"), cancellationToken);
            if (written.IsFailure) throw new InvalidOperationException("Actual nested write failed.");
            probe.FirstWriteReady.TrySetResult();
            await probe.AllowFirstReturn.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            return Result<None>.Fail(new Error(new LocalizedMessage("lockey_validation_failed")));
        }
    }
}

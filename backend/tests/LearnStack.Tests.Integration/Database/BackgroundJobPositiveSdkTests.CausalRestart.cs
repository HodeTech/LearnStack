using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
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
    public async Task Native_completed_cancellation_survives_restart_and_independent_failure_is_not_reclassified(
        bool independentFailure, bool lieOnlyOnReconcile)
    {
        await QualifyCausalRestartAsync(independentFailure, lieOnlyOnReconcile);
        if (!independentFailure && !lieOnlyOnReconcile)
            await QualifyCausalRestartAsync(false, false, forceFinalizerRetry: true);
    }

    private async Task QualifyCausalRestartAsync(bool independentFailure, bool lieOnlyOnReconcile,
        bool forceFinalizerRetry = false)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await InstallTransactionObserverAsync(database, prepared, finalized);
        await provider.SetModeAsync(resource, independentFailure ? "independent_failure_before_stop" : "cancel_on_runtime_stop");
        var originalProbe = new BackgroundJobPositiveCausalProviderProbe();
        var bounds = new BackgroundJobRuntimeOptions
        {
            SqlTimeout = TimeSpan.FromSeconds(5),
            LeaseDuration = TimeSpan.FromSeconds(20),
            RenewalInterval = TimeSpan.FromSeconds(1),
            ProgressTimeout = TimeSpan.FromSeconds(35),
            ExternalWaitTimeout = TimeSpan.FromMinutes(1),
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
            RecoveryProbeInterval = TimeSpan.FromSeconds(5),
        };
        bounds.Validate();
        using var globals = new NativeSdkGlobalFilterSnapshot();
        BackgroundJobExternalContext originalContext;
        BackgroundJobProviderCausalFacts committedProvider;
        DateTimeOffset oldLease;
        var boundary = "original_start";
        CausalRestartRowObservation? observedRow = null;

        // Original host, source, process/run gates, cancellation source and
        // provider scope finish and dispose before the replacement host exists.
        await using (var originalHost = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(originalProbe);
        }, applicationPoolSize: 32))
        await using (var originalSource = new PrivilegedDataSource(() =>
            OutboxDispatcherDataSource.Build(database.OutboxConnectionString)))
        {
            var originalRuntime = NativeSdkRuntime(originalHost.Services, originalSource, bounds);
            var submitted = await Fixture.SubmitAsync(originalHost.Services, Fixture.Parameters(resource, prepared, finalized));
            submitted.IsSuccess.Should().BeTrue();
            var job = submitted.Value;
            var originalFailed = false;
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                await originalRuntime.StartAsync(startup.Token);
                // Publication follows the actual provider acceptance COMMIT and
                // actual connector/transaction cleanup, not a fabricated report.
                boundary = "provider_accept";
                originalContext = await originalProbe.Accepted.WaitAsync(startup.Token);
                boundary = "original_facts";
                originalContext.JobId.Should().Be(job);
                originalProbe.RuntimeTokenCanBeCanceled.Should().BeTrue();
                var invocation = NativeSdkInvocation(originalRuntime);
                var execution = (BackgroundJobExecutionControl)NativeSdkField(invocation, "_execution");
                var providerTask = (Task<JobExternalExecutionResult>)NativeSdkField(execution, "_externalProvider");
                providerTask.IsCompleted.Should().BeFalse();
                execution.Token.IsCancellationRequested.Should().BeFalse();
                execution.RunGate.IsClosed.Should().BeFalse();
                var accepted = await provider.CausalFactsAsync(originalContext);
                accepted.State.Should().Be("running");
                accepted.Policy.Should().BeNull();
                accepted.Closed.Should().BeFalse();
                accepted.Evidence.Should().BeNull();
                accepted.Origin.Should().BeNull();
                accepted.Effects.Should().Be(0);
                (await provider.CountsAsync(originalContext.OperationId)).Should().Be((1L, 0L, 0L));

                if (independentFailure)
                {
                    boundary = "independent_completion";
                    // Independent work positively fails and closes its provider
                    // fence BEFORE stop; only its response remains held.
                    originalProbe.ReleaseIndependentFailure();
                    await originalProbe.CompletionStored.WaitAsync(TimeSpan.FromSeconds(15));
                    var independent = await provider.CausalFactsAsync(originalContext);
                    independent.State.Should().Be("failed");
                    independent.Policy.Should().Be("permanent");
                    independent.Origin.Should().Be("independent_failure");
                    independent.RuntimeTokenObserved.Should().BeFalse();
                    providerTask.IsCompleted.Should().BeFalse();
                    execution.RunGate.IsClosed.Should().BeFalse();
                }
                boundary = "original_stop";
                originalRuntime.RequestStop();
                execution.RunGate.IsClosed.Should().BeTrue();
                boundary = "provider_token";
                await originalProbe.RuntimeTokenObserved.WaitAsync(TimeSpan.FromSeconds(10));
                execution.Token.IsCancellationRequested.Should().BeTrue();
                boundary = "provider_completion";
                await originalProbe.CompletionStored.WaitAsync(TimeSpan.FromSeconds(15));
                originalProbe.ReleaseReport();
                boundary = "provider_report";
                var completed = await providerTask.WaitAsync(TimeSpan.FromSeconds(15));
                completed.Status.Should().Be(JobExternalExecutionStatus.Reported);
                completed.CleanupFailed.Should().BeFalse();
                completed.Observation.Should().NotBeNull();
                completed.Observation!.State.Should().Be(independentFailure
                    ? BackgroundJobExternalState.Failed : BackgroundJobExternalState.Cancelled);
                completed.Observation.FailurePolicy.Should().Be(independentFailure
                    ? BackgroundJobExternalFailurePolicy.Permanent : BackgroundJobExternalFailurePolicy.TransientNotRepeatable);
                completed.Observation.JobId.Should().Be(job);
                completed.Observation.AttemptId.Should().Be(originalContext.AttemptId);
                completed.Observation.OperationId.Should().Be(originalContext.OperationId);
                committedProvider = await provider.CausalFactsAsync(originalContext);
                committedProvider.Closed.Should().BeTrue();
                committedProvider.Evidence.Should().NotBeNull();
                committedProvider.Evidence!.Value.Should().NotBeEmpty();
                committedProvider.RuntimeTokenObserved.Should().Be(!independentFailure,
                    "the independent permanent completion predates stop; only cancellation completion observes that token causally");
                committedProvider.Origin.Should().Be(independentFailure ? "independent_failure" : "runtime_token");
                committedProvider.Deadline.Should().Be(originalContext.Deadline);
                committedProvider.Effects.Should().Be(0);
                completed.Observation.QuiescenceId.Should().Be(committedProvider.Evidence);

                // This late valid observation is retained, but the stopped old
                // owner cannot issue Outcome, release occupancy or finalize.
                typeof(BackgroundJobExecutionControl).GetField("_externalOutcome",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(execution).Should().BeNull();
                boundary = "original_retention";
                var held = await NativeUnresolvedFactsAsync(database, job);
                held.Normal.Should().Be(1);
                held.Finalize.Should().Be(0);
                held.Unreleased.Should().Be(1);
                held.Outcomes.Should().Be(0);
                held.NoStarts.Should().Be(0);
                held.Audits.Should().Be(0);
                (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
                (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
                oldLease = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.OwnershipUntil!.Value;
            }
            catch (Exception error) when (NativeWaitCatchable(error))
            {
                originalFailed = true;
                WriteCausalRestartObservation("failure", boundary, observedRow, originalRuntime, null);
                throw;
            }
            finally
            {
                await StopCausalRestartRuntimeAsync(originalRuntime, originalFailed, "original", () =>
                {
                    originalProbe.ReleaseIndependentFailure();
                    originalProbe.ReleaseReport();
                }, (kind, stage) => WriteCausalRestartObservation(kind, stage, observedRow, originalRuntime, null));
            }
            originalRuntime.Snapshot.CleanupConfirmed.Should().BeTrue();
            originalRuntime.Snapshot.RetainedInvocations.Should().Be(0);
            originalRuntime.Snapshot.Control!.Executions.Should().Be(0);
        }

        var afterOriginalJoin = await NativeUnresolvedFactsAsync(database, originalContext.JobId);
        afterOriginalJoin.Outcomes.Should().Be(0, "actual original owner cleanup must not issue a late Outcome after stop");
        afterOriginalJoin.Unreleased.Should().Be(1);
        afterOriginalJoin.Finalize.Should().Be(0);
        afterOriginalJoin.Audits.Should().Be(0);
        // Only elapsed database ownership makes recovery eligible. Elapsed time
        // does not supply the cancellation outcome or its quiescence evidence.
        await WaitForDatabaseLeaseAsync(database, oldLease);
        var freshReconcile = new BackgroundJobPositiveReconcileProbe();
        var mutant = new BackgroundJobPositiveCausalReportMutation(lieOnlyOnReconcile);
        var retry = new CausalFinalizerRetryProbe();
        await using var restartedHost = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(mutant);
            if (forceFinalizerRetry)
            {
                var request = Fixture.RequestType("Finalize");
                var contract = typeof(IRequestHandler<,>).MakeGenericType(request, typeof(Result<None>));
                var original = services.Last(service => service.ServiceType == contract).ImplementationType!;
                services.RemoveAll(contract);
                services.AddTransient(contract, provider => Activator.CreateInstance(
                    typeof(CausalFinalizerRetryHandler<>).MakeGenericType(request),
                    ActivatorUtilities.CreateInstance(provider, original), retry)!);
            }
        }, applicationPoolSize: 32, reconcileProbe: freshReconcile);
        restartedHost.Services.GetService<BackgroundJobPositiveCausalProviderProbe>().Should().BeNull(
            "the replacement provider has no original token, stop flag or causal completion probe");
        await using var restartedSource = new PrivilegedDataSource(() =>
            OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var restarted = NativeSdkRuntime(restartedHost.Services, restartedSource, bounds);
        CausalRestartFacts actual;
        long reconcileCalls = 0;
        var restartedFailed = false;
        boundary = "restart_start";
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await restarted.StartAsync(startup.Token);
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            boundary = "terminal_wait";
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, originalContext.JobId))!.Phase != "terminal_pending"
                || restarted.Snapshot.Control!.Executions != 0)
                await Task.Delay(20, completion.Token);
            boundary = "reconcile_facts";
            freshReconcile.Context.Should().Be(originalContext,
                "a new recovery token must still query the exact old provider operation/attempt/deadline");
            freshReconcile.TokenWasCancelled.Should().BeFalse();
            var calls = await provider.CountsAsync(originalContext.OperationId);
            calls.Executes.Should().Be(1);
            calls.Effects.Should().Be(0);
            calls.Reconciles.Should().BeGreaterThanOrEqualTo(1,
                "a completed report whose Outcome was refused may be reread after real recovery");
            reconcileCalls = calls.Reconciles;
            (await provider.CausalFactsAsync(originalContext)).Should().Be(committedProvider,
                "Reconcile and the deliberate adapter lie cannot rewrite the independent provider's immutable completed facts");
            boundary = "read_facts";
            actual = await ReadCausalRestartFactsAsync(database, originalContext, committedProvider.Evidence!.Value,
                value => observedRow = value, () => boundary = "transaction_witness");
            boundary = "assert_facts";
            if (forceFinalizerRetry) output.WriteLine("Causal retry counter;calls={0};forced={1}.", retry.Calls, retry.ForcedConflicts);
            actual.NormalAttempts.Should().Be(1);
            actual.FinalizeAttempts.Should().BeInRange(1, BackgroundJobRuntimeOptions.MaxFinalizeAttempts);
            if (forceFinalizerRetry) actual.FinalizeAttempts.Should().BeGreaterThanOrEqualTo(2);
            actual.SelectedNormal.Should().Be(originalContext.AttemptId.Value);
            actual.FinalizeAttempt.Should().NotBe(originalContext.AttemptId.Value);
            actual.LedgerRows.Should().Be(1L + actual.FinalizeAttempts,
                "one immutable normal attempt and every separately admitted finalizer remain in the ledger");
            actual.ReleaseCount.Should().Be(1);
            actual.ReleaseReason.Should().Be("quiescent");
            actual.BusinessIncomplete.Should().BeFalse();
            actual.TerminalAudits.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
        }
        catch (Exception error) when (NativeWaitCatchable(error))
        {
            restartedFailed = true;
            WriteCausalRestartObservation("failure", boundary, observedRow, restarted, retry);
            throw;
        }
        finally
        {
            await StopCausalRestartRuntimeAsync(restarted, restartedFailed, "restart", () => { },
                (kind, stage) => WriteCausalRestartObservation(kind, stage, observedRow, restarted, retry));
        }
        WriteCausalRestartCounts(reconcileCalls, actual.FinalizeAttempts, actual.LedgerRows,
            forceFinalizerRetry, restarted);
        restarted.Snapshot.CleanupConfirmed.Should().BeTrue();
        restarted.Snapshot.RetainedInvocations.Should().Be(0);
        restarted.Snapshot.Control!.Executions.Should().Be(0);
        if (forceFinalizerRetry)
        {
            retry.Calls.Should().BeInRange(2, BackgroundJobRuntimeOptions.MaxFinalizeAttempts);
            retry.ForcedConflicts.Should().Be(1);
            retry.Injected.Should().NotBeNull().And.NotBeSameAs(retry.Last);
            retry.Injected!.CleanupConfirmed.Should().BeTrue();
            retry.Last!.CleanupConfirmed.Should().BeTrue();
        }

        Action qualify = () => AssertCausalRestartMeaning(actual, committedProvider);
        try
        {
            var observedLeaseLoss = restarted.Snapshot.Control.LeaseLossElapsed > 0
                || restarted.Snapshot.Control.LeaseLossRenewalRefused > 0;
            if (!forceFinalizerRetry && !observedLeaseLoss)
            {
                reconcileCalls.Should().Be(1, "the unforced path has no observed lease loss requiring a reread");
                actual.FinalizeAttempts.Should().Be(1, "the unforced path has no observed lease loss or injected retry");
                actual.LedgerRows.Should().Be(2);
                mutant.Applied.Should().Be(lieOnlyOnReconcile ? 1 : 0);
            }
            if (lieOnlyOnReconcile)
            {
                ((long)mutant.Applied).Should().BeInRange(1, reconcileCalls,
                    "the dirty adapter must actually lie, at most once per real Reconcile call");
                actual.OutcomeKind.Should().Be("failed");
                actual.FailurePolicy.Should().Be("permanent");
                // This is adapter qualification, not runtime causal certification.
                // The SAME positive assertion must reject the actual dirty result.
                qualify.Should().Throw<Xunit.Sdk.XunitException>().WithMessage("*stored cancellation*");
            }
            else
            {
                mutant.Applied.Should().Be(0);
                qualify();
            }
        }
        catch (Exception error) when (NativeWaitCatchable(error))
        {
            WriteCausalRestartObservation("failure", "meaning", observedRow, restarted, retry);
            throw;
        }
    }

    private static void AssertCausalRestartMeaning(CausalRestartFacts actual, BackgroundJobProviderCausalFacts provider)
    {
        actual.OutcomeKind.Should().Be(provider.State, "the stored cancellation must survive fresh-host Reconcile unchanged");
        actual.FailurePolicy.Should().Be(provider.Policy);
        actual.FinalizationReason.Should().Be(provider.State == "cancelled" ? "external_cancelled" : "external_failed");
        actual.TerminalReason.Should().Be(provider.State == "cancelled" ? "external_retry_forbidden" : "permanent_failure");
        actual.AuditState.Should().Be(provider.State);
        actual.AuditPolicy.Should().Be(provider.Policy);
        actual.AuditModuleReason.Should().Be(actual.FinalizationReason);
        actual.AuditTerminalReason.Should().Be(actual.TerminalReason);
    }

    private static async Task<CausalRestartFacts> ReadCausalRestartFactsAsync(DisposableSchemaDatabase database,
        BackgroundJobExternalContext context, Guid providerEvidence, Action<CausalRestartRowObservation> observe,
        Action observingTransactions)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT a.outcome_kind,a.failure_policy,j.finalization_reason,j.terminal_reason,j.normal_attempts,j.finalize_attempts,
                j.final_outcome_attempt_id,j.current_attempt_id,j.business_finalization_incomplete,
                (SELECT count(*) FROM background_job_attempts x WHERE x.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id AND r.attempt_id=a.id
                    AND r.released_at IS NOT NULL AND r.released_at>=a.quiescent_at AND r.released_at>=a.outcome_at),
                (SELECT min(release_reason) FROM background_job_reservations WHERE job_id=j.id),
                (SELECT count(*) FROM audit_log x WHERE x.operation='platform.job.dead_letter' AND x.metadata->>'job_id'=@text),
                (SELECT min(metadata->>'external_state') FROM audit_log WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@text),
                (SELECT min(metadata->>'external_failure_policy') FROM audit_log WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@text),
                (SELECT min(metadata->>'finalization_reason') FROM audit_log WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@text),
                (SELECT min(metadata->>'reason') FROM audit_log WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@text)
            FROM background_jobs j JOIN background_job_attempts a ON a.id=j.final_outcome_attempt_id
            WHERE j.id=@id AND a.id=@attempt AND a.operation_id=@operation AND a.deadline=@deadline
                AND a.quiescence_id=@evidence AND a.quiescence_kind='provider_fenced'
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", context.JobId.Value);
        command.Parameters.AddWithValue("attempt", context.AttemptId.Value);
        command.Parameters.AddWithValue("operation", context.OperationId);
        command.Parameters.AddWithValue("deadline", context.Deadline);
        command.Parameters.AddWithValue("evidence", providerEvidence);
        command.Parameters.AddWithValue("text", context.JobId.Value.ToString("D"));
        CausalRestartFacts facts;
        await using (var row = await command.ExecuteReaderAsync())
        {
            (await row.ReadAsync()).Should().BeTrue("app outcome/release must bind the real provider completion and original attempt");
            observe(ReadCausalRestartObservation(row));
            facts = new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetInt32(4), row.GetInt32(5),
                row.GetGuid(6), row.GetGuid(7), row.GetBoolean(8), row.GetInt64(9), row.GetInt64(10), row.GetString(11),
                row.GetInt64(12), row.GetString(13), row.GetString(14), row.GetString(15), row.GetString(16));
        }
        observingTransactions();
        await AssertObservedTransactionsAsync(app, transaction, failure: true);
        return facts;
    }

    private sealed record CausalRestartFacts(string OutcomeKind, string FailurePolicy, string FinalizationReason,
        string TerminalReason, int NormalAttempts, int FinalizeAttempts, Guid SelectedNormal, Guid FinalizeAttempt,
        bool BusinessIncomplete, long LedgerRows, long ReleaseCount, string ReleaseReason, long TerminalAudits,
        string AuditState, string AuditPolicy, string AuditModuleReason, string AuditTerminalReason);

    // The genuine registered Finalize handler still owns all business work.
    // One actual post-write concurrency failure exercises its normal rollback
    // and fresh-admission path; no stored outcome or authority is fabricated.
    private sealed class CausalFinalizerRetryHandler<TRequest>(IRequestHandler<TRequest, Result<None>> inner,
        CausalFinalizerRetryProbe probe) : IRequestHandler<TRequest, Result<None>>
        where TRequest : notnull, IRequest<Result<None>>
    {
        public async Task<Result<None>> Handle(TRequest request, CancellationToken token)
        {
            var source = JobApplicationSourceOwner.Current
                ?? throw new InvalidOperationException("The actual causal finalizer has no application source.");
            probe.Enter(source);
            var result = await inner.Handle(request, token);
            if (result.IsSuccess && probe.TryInject(source))
                throw new AggregateConcurrencyException("Actual causal finalizer retry test conflict.");
            return result;
        }
    }

    private sealed class CausalFinalizerRetryProbe
    {
        private readonly object _sync = new();
        private int _calls, _forcedConflicts;
        internal int Calls { get { lock (_sync) return _calls; } }
        internal int ForcedConflicts { get { lock (_sync) return _forcedConflicts; } }
        internal JobApplicationSourceOwner? Injected { get; private set; }
        internal JobApplicationSourceOwner? Last { get; private set; }
        internal void Enter(JobApplicationSourceOwner source)
        {
            lock (_sync)
            {
                Last = source;
                _calls++;
            }
        }
        internal bool TryInject(JobApplicationSourceOwner source)
        {
            lock (_sync)
            {
                if (_forcedConflicts != 0) return false;
                _forcedConflicts++;
                Injected = source;
                return true;
            }
        }
    }
}

using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
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
    public Task Native_completed_cancellation_survives_restart_and_independent_failure_is_not_reclassified(
        bool independentFailure, bool lieOnlyOnReconcile)
        => QualifyCausalRestartAsync(independentFailure, lieOnlyOnReconcile);

    private async Task QualifyCausalRestartAsync(bool independentFailure, bool lieOnlyOnReconcile)
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
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                await originalRuntime.StartAsync(startup.Token);
                // Publication follows the actual provider acceptance COMMIT and
                // actual connector/transaction cleanup, not a fabricated report.
                originalContext = await originalProbe.Accepted.WaitAsync(startup.Token);
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
                originalRuntime.RequestStop();
                execution.RunGate.IsClosed.Should().BeTrue();
                await originalProbe.RuntimeTokenObserved.WaitAsync(TimeSpan.FromSeconds(10));
                execution.Token.IsCancellationRequested.Should().BeTrue();
                await originalProbe.CompletionStored.WaitAsync(TimeSpan.FromSeconds(15));
                originalProbe.ReleaseReport();
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
            finally
            {
                originalRuntime.RequestStop();
                originalProbe.ReleaseIndependentFailure();
                originalProbe.ReleaseReport();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                await originalRuntime.StopAsync(cleanup.Token);
                await originalRuntime.Completion.WaitAsync(cleanup.Token);
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
        await using var restartedHost = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(mutant);
        }, applicationPoolSize: 32, reconcileProbe: freshReconcile);
        restartedHost.Services.GetService<BackgroundJobPositiveCausalProviderProbe>().Should().BeNull(
            "the replacement provider has no original token, stop flag or causal completion probe");
        await using var restartedSource = new PrivilegedDataSource(() =>
            OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var restarted = NativeSdkRuntime(restartedHost.Services, restartedSource, bounds);
        CausalRestartFacts actual;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await restarted.StartAsync(startup.Token);
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, originalContext.JobId))!.Phase != "terminal_pending"
                || restarted.Snapshot.Control!.Executions != 0)
                await Task.Delay(20, completion.Token);
            freshReconcile.Context.Should().Be(originalContext,
                "a new recovery token must still query the exact old provider operation/attempt/deadline");
            freshReconcile.TokenWasCancelled.Should().BeFalse();
            (await provider.CountsAsync(originalContext.OperationId)).Should().Be((1L, 1L, 0L));
            (await provider.CausalFactsAsync(originalContext)).Should().Be(committedProvider,
                "Reconcile and the deliberate adapter lie cannot rewrite the independent provider's immutable completed facts");
            actual = await ReadCausalRestartFactsAsync(database, originalContext, committedProvider.Evidence!.Value);
            actual.NormalAttempts.Should().Be(1);
            actual.FinalizeAttempts.Should().Be(1);
            actual.SelectedNormal.Should().Be(originalContext.AttemptId.Value);
            actual.FinalizeAttempt.Should().NotBe(originalContext.AttemptId.Value);
            actual.LedgerRows.Should().Be(2);
            actual.ReleaseCount.Should().Be(1);
            actual.ReleaseReason.Should().Be("quiescent");
            actual.BusinessIncomplete.Should().BeFalse();
            actual.TerminalAudits.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
        }
        finally
        {
            restarted.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await restarted.StopAsync(cleanup.Token);
            await restarted.Completion.WaitAsync(cleanup.Token);
        }
        restarted.Snapshot.CleanupConfirmed.Should().BeTrue();
        restarted.Snapshot.RetainedInvocations.Should().Be(0);
        restarted.Snapshot.Control!.Executions.Should().Be(0);

        Action qualify = () => AssertCausalRestartMeaning(actual, committedProvider);
        if (lieOnlyOnReconcile)
        {
            mutant.Applied.Should().Be(1, "the dirty adapter must actually lie on the one real Reconcile call");
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
        BackgroundJobExternalContext context, Guid providerEvidence)
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
            facts = new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetInt32(4), row.GetInt32(5),
                row.GetGuid(6), row.GetGuid(7), row.GetBoolean(8), row.GetInt64(9), row.GetInt64(10), row.GetString(11),
                row.GetInt64(12), row.GetString(13), row.GetString(14), row.GetString(15), row.GetString(16));
        }
        await AssertObservedTransactionsAsync(app, transaction, failure: true);
        return facts;
    }

    private sealed record CausalRestartFacts(string OutcomeKind, string FailurePolicy, string FinalizationReason,
        string TerminalReason, int NormalAttempts, int FinalizeAttempts, Guid SelectedNormal, Guid FinalizeAttempt,
        bool BusinessIncomplete, long LedgerRows, long ReleaseCount, string ReleaseReason, long TerminalAudits,
        string AuditState, string AuditPolicy, string AuditModuleReason, string AuditTerminalReason);
}

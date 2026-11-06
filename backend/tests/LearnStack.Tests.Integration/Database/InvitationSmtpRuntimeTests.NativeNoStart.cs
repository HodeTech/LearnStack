using System.Data;
using System.Data.Common;
using FluentAssertions;
using Hangfire;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Infrastructure.Invitations;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class InvitationSmtpRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Six_actual_Identity_NoStart_admissions_reach_native_receiptless_finalization_with_atomic_MUST(bool failFirstAudit)
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(3),
            ProbeTimeout = TimeSpan.FromSeconds(3),
            ProgressTimeout = TimeSpan.FromSeconds(30),
            ControlPoll = TimeSpan.FromMilliseconds(50),
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
        };
        bounds.Validate();
        var calls = new InvitationNoStartCalls(failFirstAudit);
        await using var host = Host(smtp.Port, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(calls);
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(InvitationNoStartObservation<,>));
            services.RemoveAll<IInvitationEmailOperation>();
            services.AddScoped<IInvitationEmailOperation>(provider =>
            {
                Interlocked.Increment(ref calls.ProviderResolutions);
                return ActivatorUtilities.CreateInstance<InvitationSmtpOperation>(provider);
            });
        });
        var facts = await SeedAsync();
        await InstallInvitationNoStartTransactionObserverAsync(database, facts.Job, facts.Invitation);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var registry = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var setupGate = new BackgroundJobRuntimeGate(registry.Infrastructure);
        var setup = new PostgresBackgroundJobControlStore(source, registry, bounds, new SystemGuidFactory(), setupGate);
        ConfirmedStartReceipt? sixth = null;

        // These are six real admissions and five real NoStart writes. The fixture
        // explicitly supplies retry eligibility; it does not claim six native backoffs.
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            var admitted = await executor.ExecuteAsync(await RoutingClaim.ClaimAsync(database.OutboxConnectionString, facts.Job),
                BackgroundJobTestRun.Create());
            admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            var normal = admitted.Receipt!;
            normal.NormalAttemptOrdinal.Should().Be(ordinal);
            if (ordinal == 1)
                (await executor.ExecuteBusinessAsync(normal, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
                    .CommitAcknowledged.Should().BeTrue();
            normal.Run.Close();
            normal.SourceOwner.CloseIssuance();
            normal.SourceOwner.CleanupConfirmed.Should().BeTrue();
            await ParkInvitationNoStartAsync(database, facts.Job);
            if (ordinal == 6)
            {
                sixth = normal;
                break;
            }
            var acquired = await setup.AcquireExternalRecoveryAsync(facts.Job,
                await InvitationNoStartGenerationAsync(database, facts.Job), normal.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            acquired.CommitAcknowledged.Should().BeTrue();
            acquired.CleanupConfirmed.Should().BeTrue();
            acquired.Value.Should().NotBeNull();
            BackgroundJobExternalRecoveryBinding recovery;
            using (JobApplicationSourceOwner.Create().Enter())
                recovery = acquired.Value!.Bind(new BackgroundJobRunGate(setupGate.RunStop));
            var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
            var handle = read.Handle ?? throw new InvalidOperationException("The actual Identity setup read was refused.");
            var snapshot = handle.Require(recovery);
            snapshot.PlanJson.Should().NotBeNull();
            snapshot.ExecuteIntentRecorded.Should().BeFalse();
            var noStart = await executor.RecordExternalNoStartAsync(recovery, handle,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
            noStart.CommitAcknowledged.Should().BeTrue();
            noStart.CleanupFailed.Should().BeFalse();
            recovery.Run.Close();
            recovery.SourceOwner.CloseIssuance();
            recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET phase='pending',ownership_kind=NULL,ownership_token=NULL,ownership_until=NULL
                WHERE id=@job AND phase='reconciling'
                """, ("job", facts.Job.Value));
        }
        var before = await ReadInvitationNoStartAsync(database, facts.Job, facts.Invitation);
        before.Phase.Should().Be("reconciling");
        before.NormalAttempts.Should().Be(6);
        before.FinalizeAttempts.Should().Be(0);
        before.NormalOrdinals.Should().Equal(1, 2, 3, 4, 5, 6);
        before.NoStarts.Should().Be(5);
        before.Reservations.Should().Be(6);
        before.Released.Should().Be(5);
        before.HasPlan.Should().BeTrue();
        before.NoProviderFacts.Should().BeTrue();
        before.Status.Should().Be("Queued");
        before.SubmissionAudits.Should().Be(0);
        if (failFirstAudit) await InstallInvitationNoStartAuditFaultAsync(database);

        using var globals = new InvitationNoStartGlobalFilters();
        var gate = new BackgroundJobRuntimeGate(registry.Infrastructure);
        var ids = new SystemGuidFactory();
        var runtime = new BackgroundJobRuntime(source, registry, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            executor, new(source, registry, bounds, ids, gate), gate, bounds, ids, TimeProvider.System,
            new(false, 100), new HangfireBoundedLogProvider(), host.Services.GetRequiredService<ApplicationDataSourceLifetime>());
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            if (failFirstAudit)
            {
                await calls.RetryEntered.Task.WaitAsync(TimeSpan.FromSeconds(50));
                var failed = await ReadInvitationNoStartAsync(database, facts.Job, facts.Invitation);
                failed.Phase.Should().Be("finalizing");
                failed.NormalAttempts.Should().Be(6);
                failed.FinalizeAttempts.Should().Be(2);
                failed.NoStarts.Should().Be(6);
                failed.Released.Should().Be(6);
                failed.Status.Should().Be("Queued", "the real first Identity write must roll back with its failed MUST INSERT");
                failed.SubmissionAudits.Should().Be(0);
                failed.TerminalAudits.Should().Be(0);
                failed.DeadLettered.Should().BeFalse();
                failed.TransactionObservations.Should().Be(0, "all attempted mutation/audit observations roll back with the failed MUST INSERT");
                await using (var faultOwner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
                await using (var faultWitness = new NpgsqlCommand("SELECT last_value FROM public.invitation_nostart_audit_attempt", faultOwner))
                    ((long)(await faultWitness.ExecuteScalarAsync())!).Should().Be(1,
                        "the first actual central MUST INSERT reached the deliberately failing trigger");
                var attempts = calls.Snapshot();
                attempts.Should().HaveCount(2);
                attempts[0].Source.CleanupConfirmed.Should().BeTrue();
                attempts[0].Unit.HasActiveTransaction.Should().BeFalse();
                attempts[0].Connection.State.Should().Be(ConnectionState.Closed);
                attempts[1].Source.Should().NotBeSameAs(attempts[0].Source);
                attempts[1].Unit.Should().NotBeSameAs(attempts[0].Unit);
                calls.AllowRetry.TrySetResult();
            }
            using var completed = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!(await ReadInvitationNoStartAsync(database, facts.Job, facts.Invitation)).DeadLettered
                || runtime.Snapshot.Control is not { Executions: 0, PendingOutcomes: 0 }
                || runtime.Snapshot.RetainedInvocations != 0)
                await Task.Delay(20, completed.Token);
        }
        finally
        {
            calls.AllowRetry.TrySetResult();
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
        var after = await ReadInvitationNoStartAsync(database, facts.Job, facts.Invitation);
        after.Phase.Should().Be("terminal_pending");
        after.TerminalReason.Should().Be("retry_exhausted");
        after.NormalAttempts.Should().Be(6);
        after.FinalizeAttempts.Should().Be(failFirstAudit ? 2 : 1);
        after.NormalOrdinals.Should().Equal(1, 2, 3, 4, 5, 6);
        after.NoStarts.Should().Be(6);
        after.Reservations.Should().Be(6);
        after.Released.Should().Be(6);
        after.HasPlan.Should().BeTrue();
        after.NoProviderFacts.Should().BeTrue();
        after.SelectedNormal.Should().Be(sixth!.AttemptId.Value);
        after.FinalizationStatus.Should().Be("failed");
        after.FinalizationReason.Should().Be("normal_attempts_exhausted");
        after.Status.Should().Be("Failed");
        after.SubmissionReceipt.Should().BeNull();
        after.SubmissionAudits.Should().Be(1);
        after.TerminalAudits.Should().Be(1);
        after.TransactionObservations.Should().Be(4);
        after.AtomicSuccess.Should().BeTrue("the business mutation, Identity MUST and central terminal MUST share one commit");
        after.BusinessIncomplete.Should().BeFalse();
        calls.Prepare.Should().Be(1);
        calls.Finalize.Should().Be(failFirstAudit ? 2 : 1);
        calls.ProviderResolutions.Should().Be(0);
        smtp.Connections.Should().Be(0);
        smtp.Messages.Should().Be(0);
        var finalizers = calls.Snapshot();
        finalizers.Should().HaveCount(failFirstAudit ? 2 : 1);
        foreach (var attempt in finalizers)
        {
            attempt.Plan.Should().Be(finalizers[0].Plan);
            attempt.Plan.InvitationId.Should().Be(facts.Invitation);
            attempt.Outcome.Should().BeNull();
            attempt.Status.Should().Be(BackgroundJobFinalizationStatus.Failed);
            attempt.Reason.Should().Be(BackgroundJobFinalizationReason.NormalAttemptsExhausted);
            attempt.Source.CleanupConfirmed.Should().BeTrue();
        }
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var receiptCount = new NpgsqlCommand("SELECT count(*) FROM public.invitation_email_attempt_receipts WHERE job_id=@job", owner);
        receiptCount.Parameters.AddWithValue("job", facts.Job.Value);
        ((long)(await receiptCount.ExecuteScalarAsync())!).Should().Be(0,
            "this owner-only custody observation is not an ordinary RLS isolation claim");
    }

    private static async Task ParkInvitationNoStartAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@job;
            UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@job AND ownership_until<=clock_timestamp()
            """, ("job", job.Value));
    }

    private static async Task<long> InvitationNoStartGenerationAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("SELECT wake_generation FROM background_jobs WHERE id=@job", app, transaction);
        command.Parameters.AddWithValue("job", job.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InstallInvitationNoStartTransactionObserverAsync(DisposableSchemaDatabase database,
        BackgroundJobId job, Guid invitation)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        // pg_current_xact_id records the top-level physical owner. EF savepoints
        // can assign tuple xmin a child transaction, and later job cleanup may
        // update that tuple again; neither changes this committed observation.
        await using var command = new NpgsqlCommand($"""
            CREATE TABLE public.invitation_nostart_transactions(label text NOT NULL,root_id xid8 NOT NULL);
            GRANT INSERT,SELECT ON public.invitation_nostart_transactions TO learnstack_app;
            CREATE FUNCTION public.invitation_nostart_observe_transaction() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            DECLARE label text;
            BEGIN
                IF TG_TABLE_NAME='invitations' THEN
                    IF NEW.id='{invitation:D}'::uuid AND OLD.submission_status='Queued' AND NEW.submission_status='Failed' THEN
                        label:='invitation';
                    END IF;
                ELSIF TG_TABLE_NAME='audit_log' THEN
                    IF NEW.outcome<>'success' THEN RETURN NEW; END IF;
                    IF NEW.operation='identity.invitation.email_submission' AND NEW.entity_id='{invitation:D}' THEN
                        label:='identity-must';
                    ELSIF NEW.operation='platform.job.dead_letter' AND NEW.metadata->>'job_id'='{job.Value:D}' THEN
                        label:='terminal-must';
                    END IF;
                ELSIF NEW.id='{job.Value:D}'::uuid AND OLD.phase='finalizing' AND NEW.phase='terminal_pending' THEN
                    label:='job';
                END IF;
                IF label IS NOT NULL THEN
                    INSERT INTO public.invitation_nostart_transactions VALUES(label,pg_current_xact_id());
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER invitation_nostart_observe_domain AFTER UPDATE ON public.invitations
                FOR EACH ROW EXECUTE FUNCTION public.invitation_nostart_observe_transaction();
            CREATE TRIGGER invitation_nostart_observe_audit AFTER INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.invitation_nostart_observe_transaction();
            CREATE TRIGGER invitation_nostart_observe_job AFTER UPDATE ON public.background_jobs
                FOR EACH ROW EXECUTE FUNCTION public.invitation_nostart_observe_transaction();
            """, owner) { CommandTimeout = 5 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InstallInvitationNoStartAuditFaultAsync(DisposableSchemaDatabase database)
    {
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        // nextval survives rollback, so exactly the first real MUST INSERT fails.
        // These test-only objects belong to this fresh disposable database.
        await SchemaQueries.ExecuteAsync(owner, null, """
            CREATE SEQUENCE public.invitation_nostart_audit_attempt;
            GRANT USAGE ON SEQUENCE public.invitation_nostart_audit_attempt TO learnstack_app;
            CREATE FUNCTION public.invitation_nostart_audit_fault() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            BEGIN
                IF NEW.operation='identity.invitation.email_submission' AND NEW.outcome='success'
                    AND nextval('public.invitation_nostart_audit_attempt')=1 THEN
                    RAISE EXCEPTION 'Owned invitation finalizer audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER invitation_nostart_audit_fault BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.invitation_nostart_audit_fault();
            """);
    }

    private static async Task<InvitationNoStartFacts> ReadInvitationNoStartAsync(DisposableSchemaDatabase database,
        BackgroundJobId job, Guid invitation)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.normal_attempts,j.finalize_attempts,j.terminal_reason,j.dead_lettered_at IS NOT NULL,
                j.plan IS NOT NULL AND j.plan_fingerprint IS NOT NULL AND j.operation_id IS NOT NULL,
                j.final_outcome_attempt_id,j.finalization_status,j.finalization_reason,j.business_finalization_incomplete,
                ARRAY(SELECT a.ordinal FROM background_job_attempts a WHERE a.job_id=j.id AND a.stage='normal' ORDER BY a.ordinal),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.no_start_at IS NOT NULL),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id AND r.released_at IS NOT NULL AND r.release_reason='never_started'),
                NOT EXISTS(SELECT 1 FROM background_job_attempts a WHERE a.job_id=j.id AND
                    (a.execute_intent_at IS NOT NULL OR a.outcome IS NOT NULL OR a.outcome_kind IS NOT NULL
                     OR a.failure_policy IS NOT NULL OR a.quiescence_id IS NOT NULL OR a.quiescent_at IS NOT NULL)),
                i.submission_status,i.submission_receipt_id,
                (SELECT count(*) FROM audit_log a WHERE a.entity_id=i.id::text AND a.operation='identity.invitation.email_submission' AND a.outcome='success'),
                (SELECT count(*) FROM audit_log a WHERE a.metadata->>'job_id'=@jobtext AND a.operation='platform.job.dead_letter' AND a.outcome='success'),
                (SELECT count(*)=4 AND count(DISTINCT label)=4 AND count(DISTINCT root_id)=1
                    FROM public.invitation_nostart_transactions),
                (SELECT count(*) FROM public.invitation_nostart_transactions)
            FROM background_jobs j JOIN invitations i ON i.current_delivery_job_id=j.id WHERE j.id=@job AND i.id=@invitation
            """, app, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("job", job.Value);
        command.Parameters.AddWithValue("jobtext", job.Value.ToString("D"));
        command.Parameters.AddWithValue("invitation", invitation);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetString(0), row.GetInt32(1), row.GetInt32(2), row.IsDBNull(3) ? null : row.GetString(3),
            row.GetBoolean(4), row.GetBoolean(5), row.IsDBNull(6) ? null : row.GetGuid(6),
            row.IsDBNull(7) ? null : row.GetString(7), row.IsDBNull(8) ? null : row.GetString(8), row.GetBoolean(9),
            row.GetFieldValue<int[]>(10), row.GetInt64(11), row.GetInt64(12), row.GetInt64(13), row.GetBoolean(14),
            row.GetString(15), row.IsDBNull(16) ? null : row.GetGuid(16), row.GetInt64(17), row.GetInt64(18), row.GetBoolean(19), row.GetInt64(20));
    }

    private sealed record InvitationNoStartFacts(string Phase, int NormalAttempts, int FinalizeAttempts,
        string? TerminalReason, bool DeadLettered, bool HasPlan, Guid? SelectedNormal, string? FinalizationStatus,
        string? FinalizationReason, bool BusinessIncomplete, int[] NormalOrdinals, long NoStarts, long Reservations,
        long Released, bool NoProviderFacts, string Status, Guid? SubmissionReceipt, long SubmissionAudits,
        long TerminalAudits, bool AtomicSuccess, long TransactionObservations);

    private sealed record InvitationNoStartFinalizer(JobApplicationSourceOwner Source, IUnitOfWork Unit,
        DbConnection Connection, InvitationEmailPlan Plan, InvitationEmailOutcome? Outcome,
        BackgroundJobFinalizationStatus Status, BackgroundJobFinalizationReason Reason);

    private sealed class InvitationNoStartCalls(bool failFirstAudit)
    {
        private readonly List<InvitationNoStartFinalizer> _attempts = [];
        internal int Prepare, Finalize, ProviderResolutions;
        internal bool FailFirstAudit { get; } = failFirstAudit;
        internal TaskCompletionSource RetryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowRetry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Observe(InvitationNoStartFinalizer attempt)
        {
            lock (_attempts) { _attempts.Add(attempt); return ++Finalize; }
        }
        internal InvitationNoStartFinalizer[] Snapshot() { lock (_attempts) return _attempts.ToArray(); }
    }

    // Observation wraps the real pipeline; both Identity handlers remain registered unchanged.
    private sealed class InvitationNoStartObservation<TRequest, TResponse>(InvitationNoStartCalls calls, IUnitOfWork unit)
        : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (request is PrepareTenantInvitationEmailCommand) Interlocked.Increment(ref calls.Prepare);
            if (request is FinalizeTenantInvitationEmailCommand final)
            {
                var source = JobApplicationSourceOwner.Current ?? throw new InvalidOperationException("Identity finalizer source is missing.");
                var ordinal = calls.Observe(new(source, unit, unit.Connection, final.Finalization.Plan, final.Finalization.Outcome,
                    final.Finalization.Status, final.Finalization.Reason));
                if (ordinal == 2 && calls.FailFirstAudit)
                {
                    calls.RetryEntered.TrySetResult();
                    await calls.AllowRetry.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
            }
            return await next();
        }
    }

    private sealed class InvitationNoStartGlobalFilters : IDisposable
    {
        private readonly Hangfire.Common.JobFilter[] _filters = GlobalJobFilters.Filters.ToArray();
        public void Dispose()
        {
            GlobalJobFilters.Filters.Clear();
            foreach (var filter in _filters) GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        }
    }
}

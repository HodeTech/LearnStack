using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Email;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class InvitationSmtpRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_original_SMTP_receipt_and_fresh_recovery_preserve_the_same_evidence_in_both_orders(bool recoveryFirst)
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        var held = new HeldActualSmtp(smtp.Port, expected: 1);
        await using var originalHost = Host(smtp.Port, configure: services =>
        {
            services.RemoveAll<IEmailProvider>();
            services.AddSingleton<IEmailProvider>(held);
        });
        // Include the production organization definition in this race proof.
        var facts = await SeedAsync(SchemaFixture.OrgA1);
        var flow = await PrepareAndArmAsync(originalHost.Services, facts.Job);
        var originalCall = flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
        var recoveryBindings = new List<BackgroundJobExternalRecoveryBinding>();
        try
        {
            await held.AllAccepted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            originalCall.IsCompleted.Should().BeFalse();
            var sending = await ReadActualSmtpFactAsync(facts.Job.Value)
                ?? throw new InvalidOperationException("The actual provider claim was not observed.");
            sending.State.Should().Be("sending");
            sending.Receipt.Should().BeNull();
            sending.Evidence.Should().BeNull();
            sending.Attempt.Should().Be(flow.Receipt.AttemptId.Value);
            sending.Admission.Should().Be(flow.Receipt.Claim.Token);
            sending.Deadline.Should().Be(flow.Receipt.Deadline);
            sending.Operation.Should().Be(flow.Read.Snapshot.OperationId!.Value);
            sending.Fingerprint.Should().Be(flow.Read.Snapshot.PlanFingerprint);
            smtp.Messages.Should().Be(1);
            held.AcceptedWithCleanup.Should().Be(1);
            var before = await ReadLateSubmissionAsync(facts.Job, facts.Invitation);
            before.Should().Be(new LateSubmission("Queued", null, false, 1, 0, 0, 0));

            flow.Receipt.Run.Close();
            flow.Receipt.Run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome).Should().BeNull();
            // This is an explicit store-fixture lease-loss arrangement. It is
            // neither process restart nor an elapsed native quarantine/backoff.
            await ParkLateRecoveryAsync(facts.Job);
            await using var freshHost = Host(smtp.Port);
            freshHost.Services.GetRequiredService<InvitationSmtpProviderDataSource>()
                .Should().NotBeSameAs(originalHost.Services.GetRequiredService<InvitationSmtpProviderDataSource>());
            var registry = freshHost.Services.GetRequiredService<BackgroundJobDefinitionRegistry>();
            await using var freshSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(_database!.OutboxConnectionString));
            var runtime = new BackgroundJobRuntimeGate(registry.Infrastructure);
            var store = new PostgresBackgroundJobControlStore(freshSource, registry, new(), new SystemGuidFactory(), runtime);
            var freshExecutor = freshHost.Services.GetRequiredService<JobExecutor>();
            freshExecutor.Should().NotBeSameAs(flow.Executor);
            if (recoveryFirst)
            {
                var firstRecovery = await AcquireLateRecoveryAsync(store, runtime, facts.Job);
                recoveryBindings.Add(firstRecovery);
                AssertLateRecoveryBinding(firstRecovery, flow.Receipt);
                var firstRead = await freshExecutor.ReadExternalStateAsync(firstRecovery, CancellationToken.None);
                firstRead.Handle.Should().NotBeNull();
                var unknown = await freshExecutor.ReconcileExternalAsync(firstRecovery, firstRead.Handle!, CancellationToken.None);
                unknown.Status.Should().Be(JobExternalExecutionStatus.Reported);
                unknown.CleanupFailed.Should().BeFalse();
                unknown.Observation!.State.Should().Be(BackgroundJobExternalState.Unknown);
                unknown.Observation.QuiescenceId.Should().BeNull();
                unknown.Observation.OutcomeJson.Should().BeNull();
                (await ReadActualSmtpFactAsync(facts.Job.Value)).Should().Be(sending);
                (await ReadLateSubmissionAsync(facts.Job, facts.Invitation)).Should().Be(before);
                firstRecovery.Run.Close();
                firstRecovery.SourceOwner.CloseIssuance();
                firstRecovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
                // A second actual acquisition gets a fresh token; the first
                // recovery run cannot call Reconcile a second time.
                await ParkLateRecoveryAsync(facts.Job);
            }

            held.Release.TrySetResult();
            var late = await originalCall.WaitAsync(TimeSpan.FromSeconds(10));
            late.Status.Should().Be(JobExternalExecutionStatus.Reported);
            late.ProviderIssued.Should().BeTrue();
            late.CleanupFailed.Should().BeFalse();
            late.Observation!.State.Should().Be(BackgroundJobExternalState.Succeeded);
            var completed = (await ReadActualSmtpFactAsync(facts.Job.Value))!;
            completed.State.Should().Be("accepted");
            completed.Receipt.Should().NotBeNull();
            completed.Evidence.Should().NotBeNull();
            (completed with { State = sending.State, Receipt = null, Evidence = null }).Should().Be(sending,
                "late completion changes only terminal evidence, never the original claim or operation binding");
            late.Observation.QuiescenceId.Should().Be(completed.Evidence);
            using (var outcome = JsonDocument.Parse(late.Observation.OutcomeJson!))
                outcome.RootElement.GetProperty("ReceiptId").GetGuid().Should().Be(completed.Receipt!.Value);
            (await ReadLateSubmissionAsync(facts.Job, facts.Invitation)).Should().Be(before,
                "the provider's late durable receipt cannot release runtime occupancy or finalize Identity");
            (await flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None)).Status
                .Should().Be(JobExternalExecutionStatus.EntryRefused);

            var recovery = await AcquireLateRecoveryAsync(store, runtime, facts.Job);
            recoveryBindings.Add(recovery);
            AssertLateRecoveryBinding(recovery, flow.Receipt);
            if (recoveryBindings.Count == 2)
                recovery.Receipt.RecoveryToken.Should().NotBe(recoveryBindings[0].Receipt.RecoveryToken);
            var read = await freshExecutor.ReadExternalStateAsync(recovery, CancellationToken.None);
            read.Handle.Should().NotBeNull();
            var replay = await freshExecutor.ReconcileExternalAsync(recovery, read.Handle!, CancellationToken.None);
            replay.Status.Should().Be(JobExternalExecutionStatus.Reported);
            replay.CleanupFailed.Should().BeFalse();
            replay.Observation!.State.Should().Be(BackgroundJobExternalState.Succeeded);
            replay.Observation.OutcomeJson.Should().Be(late.Observation.OutcomeJson);
            replay.Observation.QuiescenceId.Should().Be(late.Observation.QuiescenceId);
            replay.Observation.QuiescenceKind.Should().Be(late.Observation.QuiescenceKind);
            replay.Observation.AttemptId.Should().Be(flow.Receipt.AttemptId);
            replay.Observation.OperationId.Should().Be(sending.Operation);
            replay.Observation.PlanFingerprint.Should().Be(sending.Fingerprint);
            (await ReadActualSmtpFactAsync(facts.Job.Value)).Should().Be(completed);
            (await ReadLateSubmissionAsync(facts.Job, facts.Invitation)).Should().Be(before);
            await RefuseContradictoryLateEvidenceAsync(completed);
            (await ReadActualSmtpFactAsync(facts.Job.Value)).Should().Be(completed);
            smtp.Connections.Should().Be(1);
            smtp.Messages.Should().Be(1);

            var recorded = await freshExecutor.RecordExternalOutcomeAsync(recovery, read.Handle!,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), replay.Observation, CancellationToken.None);
            recorded.CommitAcknowledged.Should().BeTrue();
            recorded.CleanupFailed.Should().BeFalse();
            var selected = await ReadLateSubmissionAsync(facts.Job, facts.Invitation);
            selected.Should().Be(before with { Released = true, Outcomes = 1 },
                "only the fresh actual Outcome checkpoint releases the reservation");
            var finalStart = await freshExecutor.ExecuteAsync(
                await RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, facts.Job), BackgroundJobTestRun.Create());
            finalStart.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            var finalized = await freshExecutor.ExecuteBusinessAsync(finalStart.Receipt!,
                finalStart.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
            finalized.CommitAcknowledged.Should().BeTrue();
            finalized.CleanupFailed.Should().BeFalse();
            (await ReadLateSubmissionAsync(facts.Job, facts.Invitation)).Should().Be(
                new LateSubmission("SubmissionAccepted", completed.Receipt, true, 1, 1, 1, 1));
            (await ReadActualSmtpFactAsync(facts.Job.Value)).Should().Be(completed);
            SmtpOwners(originalHost.Services.GetRequiredService<InvitationSmtpProviderDataSource>()).Should().BeEmpty();
            SmtpOwners(freshHost.Services.GetRequiredService<InvitationSmtpProviderDataSource>()).Should().BeEmpty();
            finalStart.Receipt!.SourceOwner.CleanupConfirmed.Should().BeTrue();
            finalStart.Receipt!.Run.Close();
            finalStart.Receipt!.SourceOwner.CloseIssuance();
        }
        finally
        {
            held.Release.TrySetResult();
            try { await originalCall; }
            finally
            {
                flow.Receipt.Run.Close();
                flow.Receipt.SourceOwner.CloseIssuance();
                flow.Receipt.SourceOwner.CleanupConfirmed.Should().BeTrue();
                foreach (var recovery in recoveryBindings)
                {
                    recovery.Run.Close();
                    recovery.SourceOwner.CloseIssuance();
                    recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
                }
            }
        }
    }

    private async Task ParkLateRecoveryAsync(BackgroundJobId job)
    {
        await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@job;
            UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,
                ownership_until=NULL,probe_after=clock_timestamp() WHERE id=@job AND ownership_until<=clock_timestamp()
            """, ("job", job.Value));
    }

    private async Task<BackgroundJobExternalRecoveryBinding> AcquireLateRecoveryAsync(
        PostgresBackgroundJobControlStore store, BackgroundJobRuntimeGate runtime, BackgroundJobId job)
    {
        long generation;
        await using (var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.OutboxConnectionString))
        await using (var query = new NpgsqlCommand("SELECT wake_generation FROM background_jobs WHERE id=@job", dispatcher))
        {
            query.Parameters.AddWithValue("job", job.Value);
            generation = (long)(await query.ExecuteScalarAsync())!;
        }
        var acquired = await store.AcquireExternalRecoveryAsync(job, generation, "external", CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.Value.Should().NotBeNull();
        using (JobApplicationSourceOwner.Create().Enter())
            return acquired.Value!.Bind(new BackgroundJobRunGate(runtime.RunStop));
    }

    private static void AssertLateRecoveryBinding(BackgroundJobExternalRecoveryBinding recovery, ConfirmedStartReceipt original)
    {
        recovery.Receipt.AttemptId.Should().Be(original.AttemptId);
        recovery.Receipt.AdmissionToken.Should().Be(original.Claim.Token);
        recovery.Receipt.RecoveryToken.Should().NotBe(original.Claim.Token);
        recovery.Receipt.Deadline.Should().Be(original.Deadline);
        recovery.SourceOwner.Should().NotBeSameAs(original.SourceOwner);
    }

    private async Task RefuseContradictoryLateEvidenceAsync(ActualSmtpFact actual)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var command = new NpgsqlCommand("""
            SELECT receipt_id FROM public.fn_invitation_smtp_complete_attempt(
                @tenant,@organization,@envelope,@invitation,@generation,@job,@attempt,@operation,@fingerprint,
                @admission,@deadline,@claim,@state,@receipt,@evidence,false)
            """, app) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant", actual.Tenant);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)actual.Organization ?? DBNull.Value);
        command.Parameters.AddWithValue("envelope", actual.Envelope);
        command.Parameters.AddWithValue("invitation", actual.Invitation);
        command.Parameters.AddWithValue("generation", actual.Generation);
        command.Parameters.AddWithValue("job", actual.Job);
        command.Parameters.AddWithValue("attempt", actual.Attempt);
        command.Parameters.AddWithValue("operation", actual.Operation);
        command.Parameters.AddWithValue("fingerprint", actual.Fingerprint);
        command.Parameters.AddWithValue("admission", actual.Admission);
        command.Parameters.AddWithValue("deadline", actual.Deadline);
        command.Parameters.AddWithValue("claim", actual.Claim);
        command.Parameters.AddWithValue("state", "accepted");
        command.Parameters.AddWithValue("receipt", actual.Receipt!.Value);
        command.Parameters.AddWithValue("evidence", actual.Evidence!.Value);
        (await command.ExecuteScalarAsync()).Should().Be(actual.Receipt.Value,
            "exact repetition of actual terminal evidence remains valid after the old lease was fenced");
        command.Parameters["state"].Value = "permanent_rejected";
        var contradiction = () => command.ExecuteScalarAsync();
        (await contradiction.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0393");
    }

    private async Task<LateSubmission> ReadLateSubmissionAsync(BackgroundJobId job, Guid invitation)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await using var query = new NpgsqlCommand("""
            SELECT i.submission_status,i.submission_receipt_id,r.released_at IS NOT NULL,j.normal_attempts,j.finalize_attempts,
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.outcome IS NOT NULL),
                (SELECT count(*) FROM audit_log a WHERE a.entity_id=@invitation AND a.outcome='success'
                    AND a.operation='identity.invitation.email_submission')
            FROM invitations i JOIN background_jobs j ON j.id=i.current_delivery_job_id
                JOIN background_job_reservations r ON r.job_id=j.id
            WHERE j.id=@job
            """, app, transaction) { CommandTimeout = 5 };
        query.Parameters.AddWithValue("job", job.Value);
        query.Parameters.AddWithValue("invitation", invitation.ToString("D"));
        await using var row = await query.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        var result = new LateSubmission(row.GetString(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.GetBoolean(2),
            row.GetInt32(3), row.GetInt32(4), row.GetInt64(5), row.GetInt64(6));
        (await row.ReadAsync()).Should().BeFalse();
        return result;
    }

    private sealed record LateSubmission(string Status, Guid? Receipt, bool Released, int Normal, int Finalize, long Outcomes, long Audits);
}

using System.Data.Common;
using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobCompletion
{
    private sealed record FinalizationRead(ConfirmedStartReceipt Receipt, JobExternalReadHandle Handle,
        BackgroundJobExternalSnapshot Snapshot);
    private FinalizationRead? _finalizationRead;

    // Only the executor binds its acknowledged, cleaned read before ordinary
    // authority/transaction entry. A bare snapshot supplies no such provenance.
    internal BackgroundJobExternalSnapshot BindFinalizationRead(ConfirmedStartReceipt receipt, JobExternalReadHandle handle)
    {
        if (receipt.Stage != BackgroundJobExecutionStage.Finalize || receipt.LedgerStage != BackgroundJobLedgerStage.Finalize
            || Unit.HasActiveTransaction || Unit.IsRollbackOnly) throw Refused();
        var snapshot = handle.Require(receipt);
        if (snapshot.SelectedNormalAttemptId != receipt.SelectedNormalAttemptId
            || snapshot.SelectedNormalAttemptId is null || snapshot.FinalizationStatus is null
            || snapshot.FinalizationReason is null || snapshot.PlanJson is null || snapshot.Envelope != receipt.Envelope)
            throw Refused();
        if (Interlocked.CompareExchange(ref _finalizationRead, new(receipt, handle, snapshot), null) is not null)
            throw Refused();
        return snapshot;
    }

    private async Task PreflightFinalizeAsync(CancellationToken token)
    {
        _ = await ReadFinalizationAsync(token).ConfigureAwait(false);
    }

    private async Task<BackgroundJobExternalSnapshot> ReadFinalizationAsync(CancellationToken token)
    {
        RequireCurrent();
        var definition = PrepareDefinition();
        var input = Volatile.Read(ref _finalizationRead);
        if (Receipt.Stage != BackgroundJobExecutionStage.Finalize || input is null
            || !ReferenceEquals(input.Receipt, Receipt)) throw Refused();
        var predicate = BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority)
            + BackgroundJobExternalCheckpointSql.Live(ExternalAuthority) + " AND j.phase='finalizing'";
        await using (var envelope = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
            "SELECT " + BackgroundJobEnvelopeSql.Projection + " FROM background_jobs j WHERE " + predicate))
        await using (var row = await envelope.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            var found = await row.ReadAsync(token).ConfigureAwait(false);
            RequireCurrent();
            if (!found || BackgroundJobEnvelopeSql.Read(row) != Receipt.Envelope) throw Refused();
        }
        RequireCurrent();
        await using var command = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
            "SELECT " + BackgroundJobStoredExternalFacts.Projection + BackgroundJobStoredExternalFacts.From + " " + predicate);
        command.Parameters.AddWithValue("normal_attempt", Receipt.SelectedNormalAttemptId!.Value.Value);
        await using var facts = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var exists = await facts.ReadAsync(token).ConfigureAwait(false);
        RequireCurrent();
        if (!exists) throw Refused();
        var stored = BackgroundJobStoredExternalFacts.Read(facts, definition, Receipt.Envelope,
            Receipt.NormalAttemptOrdinal, Receipt.SelectedNormalAttemptId.Value);
        if (stored != input.Snapshot) throw Refused();
        return stored;
    }

    private async Task FenceFinalizeAsync(IResultBase response, CancellationToken token)
    {
        RequireCurrent();
        if (response is not Result<None> { IsSuccess: true }) throw Refused();
        await using (var locked = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
            "SELECT TRUE FROM background_jobs j WHERE " + BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority) + " FOR UPDATE"))
        {
            if (await locked.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw Refused();
        }
        RequireCurrent();
        await CaptureCommitBudgetAsync(token).ConfigureAwait(false);
        var selected = await ReadFinalizationAsync(token).ConfigureAwait(false);
        RequireCurrent();
        var predicate = BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority)
            + BackgroundJobExternalCheckpointSql.Live(ExternalAuthority) + " AND j.phase='finalizing'";
        if (selected.FinalizationStatus == BackgroundJobFinalizationStatus.Succeeded)
        {
            await using var completed = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
                "UPDATE background_jobs j SET phase='completed',processed_at=clock_timestamp() WHERE " + predicate
                + " AND j.finalization_status='succeeded' AND j.finalization_reason='none' RETURNING TRUE");
            if (await completed.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw Refused();
            RequireCurrent();
            return;
        }

        var terminalReason = selected.OutcomeState is { } state
            ? BackgroundJobExternalPolicy.Select(state, selected.FailurePolicy, Receipt.NormalAttemptOrdinal).TerminalCode
            : selected.NoStartRecorded ? selected.FinalizationReason switch
            {
                BackgroundJobFinalizationReason.PermanentFailure => "permanent_failure",
                BackgroundJobFinalizationReason.NormalAttemptsExhausted when Receipt.NormalAttemptOrdinal == 6 => "retry_exhausted",
                _ => throw Refused(),
            } : throw Refused();
        if (selected.FinalizationStatus != BackgroundJobFinalizationStatus.Failed || terminalReason is null) throw Refused();
        JobTerminalTransition witness;
        await using (var terminal = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options, """
            UPDATE background_jobs j SET phase='terminal_pending',terminal_reason=@terminal,
                dead_lettered_at=clock_timestamp()
            FROM background_job_attempts sn,background_job_attempts ca
            WHERE
            """ + " " + predicate + """
             AND j.finalization_status='failed' AND NOT j.business_finalization_incomplete
             AND sn.id=j.final_outcome_attempt_id AND sn.tenant_id=j.tenant_id
             AND sn.organization_id IS NOT DISTINCT FROM j.organization_id AND sn.job_id=j.id
             AND sn.stage='normal' AND sn.ordinal=j.normal_attempts AND sn.plan_fingerprint=j.plan_fingerprint
             AND sn.operation_id=j.operation_id AND ca.id=j.current_attempt_id AND ca.tenant_id=j.tenant_id
             AND ca.organization_id IS NOT DISTINCT FROM j.organization_id AND ca.job_id=j.id
             AND ca.stage='finalize' AND ca.ordinal=j.finalize_attempts AND ca.final_outcome_attempt_id=sn.id
             AND ca.admission_token=j.ownership_token
            RETURNING j.id,j.tenant_id,j.organization_id,j.definition_key,j.queue,j.wake_generation,
                j.current_attempt_id,j.normal_attempts,j.terminal_reason,j.dead_lettered_at,old.ownership_token,
                j.profile,j.phase,j.processed_at,j.finalize_attempts,j.final_outcome_attempt_id,
                sn.stage,sn.ordinal,sn.outcome_kind,sn.failure_policy,sn.quiescent_at,sn.no_start_at,
                j.finalization_status,j.finalization_reason,j.business_finalization_incomplete,
                ca.stage,ca.ordinal,ca.final_outcome_attempt_id,ca.admission_token,ca.admitted_at,ca.deadline,
                old.phase,old.ownership_kind,old.ownership_until,old.attempt_deadline,current_user::text,clock_timestamp()
            """))
        {
            terminal.Parameters.AddWithValue("terminal", terminalReason);
            await using var row = await terminal.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await row.ReadAsync(token).ConfigureAwait(false)) throw Refused();
            witness = FinalizationTerminalMint.Mint(this, row);
        }
        RequireCurrent();
        var draft = new AuditEntryDraft
        {
            Id = AuditEntryId.From(ids.NewUuidV7()),
            TenantId = Receipt.Envelope.TenantId,
            OrganizationId = Receipt.Envelope.OrganizationId,
            ActorUserId = UserId.SystemActor,
            ActorEmail = null,
            ModuleName = "platform",
            Operation = "platform.job.dead_letter",
            OperationClass = OperationClass.Must,
            OperationType = OperationType.SecurityEvent,
            Outcome = AuditOutcome.Success,
            EntityType = null,
            EntityId = null,
            ErrorKey = null,
            Reason = null,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            IpAddress = null,
            UserAgent = null,
            CorrelationId = witness.CorrelationId,
            Timestamp = witness.DeadLetteredAt,
            Metadata = witness.Metadata,
        };
        await audit.WriteJobTerminalAsync(draft, witness, Unit.Connection, Unit.Transaction!, token).ConfigureAwait(false);
        RequireCurrent();
    }

    // The ordinary participant cannot inherit the kernel minter. This private
    // bridge is its sole app-row mint, guarded by the existing exact root owner.
    private sealed class FinalizationTerminalMint : JobTerminalTransition.MintingSource
    {
        internal static JobTerminalTransition Mint(PostgresBackgroundJobCompletion owner, DbDataReader row)
        {
            owner.RequireCurrent();
            var witness = FromAppFinalizationUpdate(owner.Unit, row, Activity.Current?.Id ?? throw Refused());
            owner.RequireCurrent();
            return witness;
        }
    }
}

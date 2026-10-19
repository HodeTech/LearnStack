using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using System.Diagnostics;

namespace LearnStack.Infrastructure.Jobs;

internal sealed record BackgroundJobCheckpointHistoryObservation(BackgroundJobCheckpointHistoryDisposition Disposition,
    BackgroundJobCheckpointHistoryFacts Facts, BackgroundJobCheckpointConfirmation? Confirmation);

internal sealed partial class PostgresBackgroundJobControlStore
{
    // Original ledger identity, never current_attempt_id. The job lock is taken
    // and its reader finished by a preceding, separate SQL statement.
    private const string CheckpointHistoryColumns = """
        SELECT j.id,j.tenant_id,j.organization_id,j.definition_key,j.profile,j.queue,j.capacity_group,j.original_due_at,
            a.id,a.stage,a.ordinal,a.admission_token,a.admitted_at,a.deadline,a.final_outcome_attempt_id,
            j.plan_fingerprint,j.operation_id,j.prepared_at,a.plan_fingerprint,a.operation_id,a.execute_intent_at,
            a.outcome_kind,a.outcome_at,a.failure_policy,a.quiescence_id,a.quiescence_kind,a.quiescent_at,a.no_start_at,
            r.attempt_id,r.released_at,r.release_reason,j.final_outcome_attempt_id,j.finalization_status,j.finalization_reason,
            j.current_attempt_id,j.normal_attempts,j.finalize_attempts,j.phase,j.processed_at,j.dead_lettered_at,
            j.terminal_reason,j.business_finalization_incomplete,j.wake_generation,j.ownership_token,j.ownership_kind,
            j.ownership_until,j.attempt_deadline,clock_timestamp()
        FROM background_jobs j JOIN background_job_attempts a
            ON a.tenant_id=j.tenant_id AND a.job_id=j.id
                AND a.organization_id IS NOT DISTINCT FROM j.organization_id
        LEFT JOIN background_job_reservations r
            ON a.stage='normal' AND r.tenant_id=a.tenant_id AND r.job_id=a.job_id
                AND r.attempt_id=a.id AND r.capacity_group=j.capacity_group
        """;
    private const string CheckpointHistoryProjection = CheckpointHistoryColumns + " WHERE j.id=@id AND a.id=@attempt";

    internal async Task<OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>> ProbeExternalCheckpointAsync(
        BackgroundJobCheckpointHistoryOrigin origin, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(origin);
        var resources = new BackgroundJobControlResources { IsProbe = true };
        CheckpointHistoryCandidate? candidate = null;
        var disposition = BackgroundJobCheckpointHistoryDisposition.Unresolved;
        using var deadline = Deadline(cancellationToken, probe: true);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false)
                && await LockExternalJobAsync(resources, origin.JobId.Value, deadline.Token).ConfigureAwait(false))
            {
                var observedAt = Stopwatch.GetTimestamp();
                var command = Command(resources, CheckpointHistoryProjection);
                command.Parameters.AddWithValue("id", origin.JobId.Value);
                command.Parameters.AddWithValue("attempt", origin.AttemptId.Value);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                    candidate = origin.Receipt is { } receipt ? CaptureCheckpointHistory(receipt, origin.Gate, observedAt, resources.Reader)
                        : origin.Checkpoint!.Arm is { } arm ? CaptureCheckpointHistory(arm, observedAt, resources.Reader)
                        : CaptureCheckpointHistory(origin.Checkpoint.Outcome!, observedAt, resources.Reader);
                await FinishAsync(resources).ConfigureAwait(false);
                if (candidate is not null && origin.Matches(candidate.Facts) && ValidHistoryIdentity(candidate.Facts))
                    disposition = origin.Checkpoint?.Outcome is { IsTerminalNoStart: true }
                        ? await ClassifyTerminalNoStartHistoryAsync(resources, origin, candidate.Facts, deadline.Token).ConfigureAwait(false)
                        : origin.Kind == BackgroundJobCheckpointKind.Finalize
                            ? await ClassifyFinalizationHistoryAsync(resources, origin, candidate.Facts, deadline.Token).ConfigureAwait(false)
                            : ClassifyNormalHistory(origin, candidate.Facts);
                await resources.Transaction!.RollbackAsync(deadline.Token).ConfigureAwait(false);
                resources.ProbeRollbackAcknowledged = true;
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        BackgroundJobCheckpointHistoryObservation? value = null;
        if (candidate is not null && resources.ProbeRollbackAcknowledged && resources.CleanupConfirmed && resources.Failure is null)
        {
            var confirmation = disposition == BackgroundJobCheckpointHistoryDisposition.Committed
                ? PublishCheckpointHistory(candidate) : null;
            value = new(disposition, candidate.Facts, confirmation);
        }
        return new(value, resources, Array.Empty<BackgroundJobCommitProbe>(), (origin, candidate));
    }

    private static bool ValidHistoryIdentity(BackgroundJobCheckpointHistoryFacts row) =>
        row.JobId.Version == 7 && row.AttemptId.Version == 7 && row.TenantId != Guid.Empty
        && row.TenantId != TenantId.PlatformSentinel.Value && row.OrganizationId != Guid.Empty
        && row.Profile == "external" && BackgroundJobNames.Definition(row.DefinitionKey)
        && BackgroundJobNames.QueueOrPool(row.Queue) && BackgroundJobNames.ModuleResource(row.CapacityGroup)
        && row.Stage is "normal" or "finalize" && row.Ordinal is >= 1 and <= 6
        && row.AdmissionToken != Guid.Empty && row.AdmittedAt >= row.OriginalDueAt && row.AdmittedAt <= row.DbNow
        && row.Deadline > row.AdmittedAt && row.Deadline - row.AdmittedAt <= TimeSpan.FromHours(24)
        && row.NormalAttempts is >= 1 and <= 6 && row.FinalizeAttempts is >= 0 and <= 6
        && (row.Stage == "normal" ? row.Ordinal <= row.NormalAttempts : row.Ordinal <= row.FinalizeAttempts)
        && row.CurrentWakeGeneration >= 1 && row.CurrentAttemptId?.Version == 7
        && (row.PreparedAt is null || row.PreparedAt <= row.DbNow);

    private static BackgroundJobCheckpointHistoryDisposition ClassifyNormalHistory(BackgroundJobCheckpointHistoryOrigin origin,
        BackgroundJobCheckpointHistoryFacts row)
    {
        if (!ValidNormalHistory(row)) return BackgroundJobCheckpointHistoryDisposition.Unresolved;
        var stamped = row.AttemptPlanFingerprint is not null;
        if (origin.Checkpoint?.Arm is { } arm && (row.PlanFingerprint != arm.PlanFingerprint || row.OperationId != arm.OperationId)
            || origin.Checkpoint?.Outcome is { IsNoStart: false } outcome
                && (row.PlanFingerprint != outcome.PlanFingerprint || row.OperationId != outcome.OperationId))
            return BackgroundJobCheckpointHistoryDisposition.Unresolved;
        return origin.Kind switch
        {
            BackgroundJobCheckpointKind.Prepare => stamped ? BackgroundJobCheckpointHistoryDisposition.Committed
                : BackgroundJobCheckpointHistoryDisposition.Absent,
            BackgroundJobCheckpointKind.Arm => row.ExecuteIntentAt is not null ? BackgroundJobCheckpointHistoryDisposition.Committed
                : row.NoStartAt is not null ? BackgroundJobCheckpointHistoryDisposition.OtherDurableBranch
                : BackgroundJobCheckpointHistoryDisposition.Absent,
            BackgroundJobCheckpointKind.Outcome => row.OutcomeKind is not null ? ClassifyOutcomeHistory(origin, row)
                : row.NoStartAt is not null ? BackgroundJobCheckpointHistoryDisposition.OtherDurableBranch
                : BackgroundJobCheckpointHistoryDisposition.Absent,
            BackgroundJobCheckpointKind.NoStart => row.NoStartAt is not null
                ? row.Ordinal == 6 && row.PlanFingerprint is not null
                    && !MatchesNormalSelection(row, new(BackgroundJobKnownOutcomeDisposition.Finalize,
                        BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.NormalAttemptsExhausted, "retry_exhausted"))
                        ? BackgroundJobCheckpointHistoryDisposition.Unresolved : BackgroundJobCheckpointHistoryDisposition.Committed
                : row.ExecuteIntentAt is not null ? BackgroundJobCheckpointHistoryDisposition.OtherDurableBranch
                : BackgroundJobCheckpointHistoryDisposition.Absent,
            _ => BackgroundJobCheckpointHistoryDisposition.Unresolved,
        };
    }

    private static BackgroundJobCheckpointHistoryDisposition ClassifyOutcomeHistory(
        BackgroundJobCheckpointHistoryOrigin origin, BackgroundJobCheckpointHistoryFacts row)
    {
        var decision = HistoryOutcomeDecision(row);
        // An old completed marker alone cannot confirm a selection-only COMMIT.
        // The selected normal pointer remains immutable after finalize admission.
        if (decision.Disposition == BackgroundJobKnownOutcomeDisposition.Finalize
            ? !MatchesNormalSelection(row, decision) : origin.Checkpoint?.Outcome?.IsStoredCompleted == true)
            return BackgroundJobCheckpointHistoryDisposition.Unresolved;
        return BackgroundJobCheckpointHistoryDisposition.Committed;
    }

    private static bool MatchesNormalSelection(BackgroundJobCheckpointHistoryFacts row, BackgroundJobKnownOutcomeDecision decision) =>
        decision.Disposition == BackgroundJobKnownOutcomeDisposition.Finalize
        && row.JobSelectedNormalAttemptId == row.AttemptId && row.NormalAttempts == row.Ordinal
        && row.PlanFingerprint is not null && row.AttemptPlanFingerprint == row.PlanFingerprint
        && row.AttemptOperationId == row.OperationId
        && row.FinalizationStatus == (decision.FinalizationStatus == BackgroundJobFinalizationStatus.Succeeded ? "succeeded" : "failed")
        && row.FinalizationReason == (decision.FinalizationReason switch
        {
            BackgroundJobFinalizationReason.None => "none",
            BackgroundJobFinalizationReason.ExternalFailed => "external_failed",
            BackgroundJobFinalizationReason.ExternalCancelled => "external_cancelled",
            BackgroundJobFinalizationReason.NormalAttemptsExhausted => "normal_attempts_exhausted",
            _ => throw BackgroundJobExternalCheckpointSql.Refused(),
        });

    private static bool ValidPlanHistory(BackgroundJobCheckpointHistoryFacts row)
    {
        if (row.PlanFingerprint is null)
            return row.OperationId is null && row.PreparedAt is null
                && row.AttemptPlanFingerprint is null && row.AttemptOperationId is null;
        if (row.PreparedAt is null || row.PreparedAt > row.DbNow
            || row.OperationId != BackgroundJobExternalIdentity.OperationId(BackgroundJobId.From(row.JobId), row.PlanFingerprint))
            return false;
        return row.AttemptPlanFingerprint is null ? row.AttemptOperationId is null
            : row.AttemptPlanFingerprint == row.PlanFingerprint && row.AttemptOperationId == row.OperationId;
    }

    private static bool ValidNormalHistory(BackgroundJobCheckpointHistoryFacts row)
    {
        if (row.Stage != "normal" || row.SelectedNormalAttemptId is not null || !ValidPlanHistory(row)
            || row.ReservationAttemptId != row.AttemptId || row.ExecuteIntentAt > row.DbNow || row.NoStartAt > row.DbNow
            || row.ExecuteIntentAt < row.AdmittedAt || row.NoStartAt < row.AdmittedAt
            || row.ExecuteIntentAt is not null && (row.AttemptPlanFingerprint is null || row.NoStartAt is not null)) return false;
        if (row.OutcomeKind is null)
        {
            if (row.OutcomeAt is not null || row.FailurePolicy is not null || row.QuiescenceId is not null
                || row.QuiescenceKind is not null || row.QuiescentAt is not null) return false;
            return row.NoStartAt is not null
                ? row.ExecuteIntentAt is null && row.ReleasedAt >= row.NoStartAt && row.ReleasedAt <= row.DbNow
                    && row.ReleaseReason == "never_started"
                : row.ReleasedAt is null && row.ReleaseReason is null;
        }
        _ = HistoryOutcomeDecision(row);
        return row.ExecuteIntentAt is not null && row.NoStartAt is null && row.AttemptPlanFingerprint is not null
            && row.OutcomeAt >= row.ExecuteIntentAt && row.OutcomeAt <= row.DbNow
            && row.QuiescenceId is { } evidence && evidence != Guid.Empty
            && row.QuiescenceKind is "provider_fenced" or "execution_terminated"
            && row.QuiescentAt >= row.ExecuteIntentAt && row.QuiescentAt <= row.DbNow
            && row.ReleasedAt >= row.QuiescentAt && row.ReleasedAt <= row.DbNow && row.ReleaseReason == "quiescent";
    }

    private static BackgroundJobKnownOutcomeDecision HistoryOutcomeDecision(BackgroundJobCheckpointHistoryFacts row) =>
        BackgroundJobExternalPolicy.Select(row.OutcomeKind switch
        {
            "succeeded" => BackgroundJobExternalState.Succeeded,
            "failed" => BackgroundJobExternalState.Failed,
            "cancelled" => BackgroundJobExternalState.Cancelled,
            _ => throw BackgroundJobExternalCheckpointSql.Refused(),
        }, row.FailurePolicy switch
        {
            null => null,
            "permanent" => BackgroundJobExternalFailurePolicy.Permanent,
            "transient_repeatable" => BackgroundJobExternalFailurePolicy.TransientRepeatable,
            "transient_not_repeatable" => BackgroundJobExternalFailurePolicy.TransientNotRepeatable,
            _ => throw BackgroundJobExternalCheckpointSql.Refused(),
        }, row.Ordinal);

    private async Task<BackgroundJobCheckpointHistoryDisposition> ClassifyFinalizationHistoryAsync(
        BackgroundJobControlResources resources, BackgroundJobCheckpointHistoryOrigin origin,
        BackgroundJobCheckpointHistoryFacts row, CancellationToken token)
    {
        var receipt = origin.Receipt!;
        if (row.SelectedNormalAttemptId != receipt.SelectedNormalAttemptId?.Value
            || row.NormalAttempts != receipt.NormalAttemptOrdinal
            || await ReadSelectedFinalizationAsync(resources, row, token).ConfigureAwait(false) is not { } selected)
            return BackgroundJobCheckpointHistoryDisposition.Unresolved;
        // A successor's applied finalizer never confirms this old attempt's
        // COMMIT. Valid selected history with a changed owner is another branch,
        // not authority to retry or remint an ordinary root from the old gate.
        if (row.CurrentAttemptId != row.AttemptId || row.FinalizeAttempts != receipt.FinalizeAttemptOrdinal)
            return BackgroundJobCheckpointHistoryDisposition.OtherDurableBranch;
        if (row.BusinessFinalizationIncomplete) return BackgroundJobCheckpointHistoryDisposition.OtherDurableBranch;
        if (selected.Decision.FinalizationStatus == BackgroundJobFinalizationStatus.Succeeded)
        {
            if (row.Phase == "completed" && row.ProcessedAt is not null && row.ProcessedAt <= row.DbNow
                && row.DeadLetteredAt is null && row.TerminalReason is null)
                return BackgroundJobCheckpointHistoryDisposition.Committed;
        }
        else if (row.Phase == "terminal_pending" && row.DeadLetteredAt is not null && row.DeadLetteredAt <= row.DbNow
            && row.ProcessedAt is null && row.TerminalReason == selected.Decision.TerminalCode)
            return BackgroundJobCheckpointHistoryDisposition.Committed;
        return row.Phase is "finalizing" or "reconciling" or "finalize_pending"
            && row.ProcessedAt is null && row.DeadLetteredAt is null && row.TerminalReason is null
            ? BackgroundJobCheckpointHistoryDisposition.Absent : BackgroundJobCheckpointHistoryDisposition.Unresolved;
    }

    private async Task<(BackgroundJobCheckpointHistoryFacts Normal, BackgroundJobKnownOutcomeDecision Decision)?> ReadSelectedFinalizationAsync(
        BackgroundJobControlResources resources, BackgroundJobCheckpointHistoryFacts row, CancellationToken token)
    {
        if (row.Stage != "finalize" || row.JobSelectedNormalAttemptId != row.SelectedNormalAttemptId
            || row.SelectedNormalAttemptId is null || row.SelectedNormalAttemptId == row.AttemptId
            || !ValidPlanHistory(row) || row.PlanFingerprint is null || row.ExecuteIntentAt is not null
            || row.OutcomeKind is not null || row.OutcomeAt is not null || row.FailurePolicy is not null
            || row.QuiescenceId is not null || row.QuiescenceKind is not null || row.QuiescentAt is not null
            || row.NoStartAt is not null || row.ReservationAttemptId is not null
            || row.ReleasedAt is not null || row.ReleaseReason is not null) return null;
        var command = Command(resources, CheckpointHistoryProjection);
        command.Parameters.AddWithValue("id", row.JobId);
        command.Parameters.AddWithValue("attempt", row.SelectedNormalAttemptId.Value);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        BackgroundJobCheckpointHistoryFacts? normal = null;
        if (await resources.Reader!.ReadAsync(token).ConfigureAwait(false)) normal = CaptureSelectedNormalHistory(resources.Reader);
        await FinishAsync(resources).ConfigureAwait(false);
        if (normal is null || !ValidHistoryIdentity(normal) || !ValidNormalHistory(normal)
            || normal.Ordinal != row.NormalAttempts || normal.AttemptId != row.SelectedNormalAttemptId
            || normal.PlanFingerprint != row.PlanFingerprint || normal.OperationId != row.OperationId) return null;
        var selected = normal.OutcomeKind is not null ? HistoryOutcomeDecision(normal)
            : normal.NoStartAt is not null && row.FinalizationStatus == "failed" && row.FinalizationReason == "permanent_failure"
                ? new BackgroundJobKnownOutcomeDecision(BackgroundJobKnownOutcomeDisposition.Finalize,
                    BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.PermanentFailure, "permanent_failure")
            : normal.NoStartAt is not null && normal.Ordinal == 6
                ? new BackgroundJobKnownOutcomeDecision(BackgroundJobKnownOutcomeDisposition.Finalize,
                    BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.NormalAttemptsExhausted, "retry_exhausted")
                : null;
        if (selected?.Disposition != BackgroundJobKnownOutcomeDisposition.Finalize
            || row.FinalizationStatus != (selected.FinalizationStatus == BackgroundJobFinalizationStatus.Succeeded ? "succeeded" : "failed")
            || row.FinalizationReason != (selected.FinalizationReason switch
            {
                BackgroundJobFinalizationReason.None => "none",
                BackgroundJobFinalizationReason.ExternalFailed => "external_failed",
                BackgroundJobFinalizationReason.ExternalCancelled => "external_cancelled",
                BackgroundJobFinalizationReason.NormalAttemptsExhausted => "normal_attempts_exhausted",
                BackgroundJobFinalizationReason.PermanentFailure => "permanent_failure",
                _ => throw BackgroundJobExternalCheckpointSql.Refused(),
            })) return null;
        return (normal, selected);
    }
}

using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Actual immutable metadata, never payload or execution authority.</summary>
internal sealed record BackgroundJobFinalizeControlHistory(BackgroundJobCheckpointHistoryFacts Finalize,
    BackgroundJobCheckpointHistoryFacts Selected, BackgroundJobKnownOutcomeDecision Selection);

internal sealed partial class PostgresBackgroundJobControlStore
{
    /// <summary>Dispose one positively cleaned ordinary Finalize; never admit or invoke another root.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> FinishExternalFinalizeAsync(
        BackgroundJobExecutionIdentity identity, JobBusinessExecutionResult execution,
        OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>? reconciliation,
        CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(execution);
        var receipt = execution.Receipt;
        if (identity.Profile != "external" || identity.LedgerStage != BackgroundJobLedgerStage.Finalize
            || identity.IsRecovery || identity.Stage != BackgroundJobExecutionStage.Finalize
            || receipt.Stage != BackgroundJobExecutionStage.Finalize || receipt.LedgerStage != BackgroundJobLedgerStage.Finalize
            || receipt.AttemptId != identity.AttemptId || receipt.Claim.Token != identity.Token
            || receipt.Claim.WakeGeneration != identity.WakeGeneration || receipt.Envelope.JobId != identity.JobId
            || receipt.FinalizeAttemptOrdinal != identity.FinalizeAttemptOrdinal
            || receipt.NormalAttemptOrdinal != identity.NormalAttemptOrdinal
            || receipt.SelectedNormalAttemptId != identity.SelectedNormalAttemptId
            || !ReferenceEquals(receipt.Run, execution.Gate.Run) || execution.Gate.Kind != BackgroundJobCheckpointKind.Finalize
            || !execution.ExecutionIssued || execution.CommitAcknowledged || execution.CleanupFailed
            || !execution.SourceOwner.CleanupConfirmed)
            throw new ArgumentException("Finalizer disposition requires the exact cleaned ordinary attempt.", nameof(execution));
        BackgroundJobFailureDecision decision;
        if (execution.DurableOutcomeUncertain)
        {
            if (reconciliation is not { CleanupConfirmed: true, Failed: false, Value: { } observed }
                || observed.Disposition != BackgroundJobCheckpointHistoryDisposition.Absent
                || !BackgroundJobCheckpointHistoryOrigin.ForFinalize(execution).Matches(observed.Facts))
                throw new ArgumentException("An uncertain finalizer requires exact locked absence.", nameof(reconciliation));
            decision = new(BackgroundJobFailureDisposition.Retry, "unexpected_fault", null);
        }
        else
        {
            if (!execution.KnownRollback || reconciliation is not null)
                throw new ArgumentException("Finalizer failure requires positive original rollback.", nameof(execution));
            decision = execution.Decision;
        }
        if (!(decision.Disposition == BackgroundJobFailureDisposition.Retry
                && decision.ErrorCode is "concurrency_conflict" or "rate_limited" or "dependency_unavailable" or "audit_unavailable" or "unexpected_fault"
                && decision.TerminalReason is null
            || decision.Disposition == BackgroundJobFailureDisposition.Terminal
                && decision.ErrorCode == "permanent_failure" && decision.TerminalReason == BackgroundJobTerminalReason.PermanentFailure))
            throw new ArgumentException("Finalizer disposition requires the closed invocation classifier.", nameof(execution));

        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        BackgroundJobControlChange? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                // A skipped lock cannot discard the original permanent/retry
                // classifier. Wait within the bounded SQL operation; timeout
                // remains a failed request retained by the completion queue.
                var row = await LockRowAsync(resources, identity.JobId.Value, deadline.Token).ConfigureAwait(false);
                if (row is not null && row.Matches(identity) && row.LedgerToken == identity.AdmissionToken
                    && row.Phase == "finalizing" && row.Incomplete && ActiveExternalFinalizer(row)
                    && await ReadFinalizerControlHistoryAsync(resources, row, deadline.Token).ConfigureAwait(false) is { } history)
                {
                    var changed = await DisposeFinalizerAsync(resources, row, history,
                        decision.Disposition == BackgroundJobFailureDisposition.Terminal ? "finalize_refused" : null,
                        decision.ErrorCode!, revokeCleanedOwner: true, deadline.Token).ConfigureAwait(false);
                    if (changed is { } disposed)
                    {
                        value = disposed.Change;
                        probes.Add(disposed.Probe);
                        deadline.Token.ThrowIfCancellationRequested();
                        if (runtimeGate?.TryAdmit() != false) await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(resources.CommitAcknowledged ? value : null, resources, probes.AsReadOnly(), (execution, reconciliation));
    }

    private bool ActiveExternalFinalizer(ControlRow row) =>
        definitions.Resolve(row.DefinitionKey) is
        {
            Disposition: BackgroundJobDefinitionDisposition.Active,
            Definition: { Profile: BackgroundJobExecutionProfile.External } definition
        }
        && definition.Queue == row.Queue && definition.CapacityGroup == row.CapacityGroup
        && (definition.Scope == BackgroundJobScope.TenantWide) == (row.OrganizationId is null);

    private static string ExternalFinalizeSelectionSql() => """
        SELECT j.id FROM background_jobs j
        WHERE j.profile='external' AND j.capacity_group IS NOT NULL AND j.current_attempt_id IS NOT NULL
            AND j.normal_attempts BETWEEN 1 AND 6 AND j.finalize_attempts BETWEEN 1 AND 6
            AND j.final_outcome_attempt_id IS NOT NULL AND j.finalization_status IS NOT NULL
            AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL AND j.terminal_reason IS NULL
            AND j.ownership_token IS NOT NULL AND j.phase<>'quarantined'
            AND j.ownership_until<=statement_timestamp()
            AND ((j.phase='finalizing' AND j.ownership_kind='execution')
                OR (j.phase='reconciling' AND j.ownership_kind='recovery'))
            AND EXISTS (SELECT 1 FROM unnest(@externalDefinitions,@externalQueues,@externalGroups,@externalTenantWide)
                AS definition(key,queue,capacity,tenant_wide)
                WHERE definition.key=j.definition_key AND definition.queue=j.queue
                    AND definition.capacity=j.capacity_group AND definition.tenant_wide=(j.organization_id IS NULL))
            AND NOT (j.id=ANY(@blocked))
        ORDER BY j.ownership_until,j.created_at,j.id LIMIT @batch FOR UPDATE SKIP LOCKED
        """;

    private async Task<(BackgroundJobControlChange Change, BackgroundJobCommitProbe Probe)?> RecoverExternalFinalizeAsync(
        BackgroundJobControlResources resources, ControlRow row, CancellationToken token)
    {
        // Finalize never owns a fresh capacity reservation. This path must run
        // before the generic recovery branch selected by CapacityGroup.
        if (!row.Incomplete || !ActiveExternalFinalizer(row)
            || await ReadFinalizerControlHistoryAsync(resources, row, token).ConfigureAwait(false) is not { } history)
            return null;
        return await DisposeFinalizerAsync(resources, row, history, null, "unexpected_fault",
            revokeCleanedOwner: false, token).ConfigureAwait(false);
    }

    private async Task<(BackgroundJobControlChange Change, BackgroundJobCommitProbe Probe)?> DisposeFinalizerAsync(
        BackgroundJobControlResources resources, ControlRow row, BackgroundJobFinalizeControlHistory history,
        string? refusal, string errorCode, bool revokeCleanedOwner, CancellationToken token)
    {
        if (!row.Incomplete || row.Profile != "external" || !row.MatchingAttempt || row.LedgerStage != "finalize"
            || row.FinalizeAttempts is < 1 or > 6 || refusal is not (null or "finalize_refused")) return null;
        // A retained permanent refusal keeps precedence even on admission six
        // or after queue/lock delay expires its unchanged owner. Fresh crash or
        // expiry recovery has no such original decision and consumes the stage.
        var reason = refusal ?? (row.FinalizeAttempts == 6 ? "finalize_exhausted" : null);
        var origin = FinalizerProbe(row, history, reason);
        var noOwnerExhaustion = !revokeCleanedOwner && row.Phase == "finalize_pending"
            && row.FinalizeAttempts == 6 && row.OwnershipToken is null;
        var owner = row.OwnershipToken;
        if (!noOwnerExhaustion)
        {
            if (!(row.Phase == "finalizing" && row.OwnershipKind == "execution" && owner == row.LedgerToken
                || !revokeCleanedOwner && row.Phase == "reconciling" && row.OwnershipKind == "recovery"
                    && owner is not null && owner != row.LedgerToken)) return null;
            if (revokeCleanedOwner)
            {
                // Shorten only this exact positively cleaned owner if it is
                // still live. If queue/lock delay already expired it, this is a
                // no-op and the separately fenced acquisition below preserves
                // its original decision. Expiry cannot reclassify that result.
                var expire = Command(resources, """
                    WITH finalizer_clock AS MATERIALIZED (SELECT clock_timestamp() AS at)
                    UPDATE background_jobs j SET ownership_until=finalizer_clock.at FROM finalizer_clock WHERE
                    """ + " " + ExternalFinalizeFence + " " + """
                        AND j.phase='finalizing' AND j.ownership_kind='execution'
                        AND j.ownership_until>finalizer_clock.at
                    """);
                AddFinalizerFence(expire, origin, owner);
                await expire.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await resources.FinishCommandAsync().ConfigureAwait(false);
            }
            var recovery = ids.NewUuidV4();
            if (recovery == Guid.Empty || recovery == owner || recovery == row.LedgerToken)
                throw new InvalidOperationException("Finalizer recovery requires a fresh non-executing token.");
            var acquire = Command(resources, """
                UPDATE background_jobs j SET phase='reconciling',ownership_token=@recovery,
                    ownership_kind='recovery',ownership_until=clock_timestamp()+@lease,probe_after=NULL WHERE
                """ + " " + ExternalFinalizeFence + " " + """
                    AND j.phase=@phase AND j.ownership_kind=@kind AND j.ownership_until<=clock_timestamp()
                """);
            AddFinalizerFence(acquire, origin, owner);
            acquire.Parameters.AddWithValue("phase", row.Phase);
            acquire.Parameters.AddWithValue("kind", row.OwnershipKind!);
            acquire.Parameters.AddWithValue("recovery", recovery);
            acquire.Parameters.AddWithValue("lease", options.LeaseDuration);
            var acquired = await acquire.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            await resources.FinishCommandAsync().ConfigureAwait(false);
            if (!acquired)
            {
                // A cleaned original decision cannot be discarded after this
                // locked handoff attempt. Failure also rolls back any preceding
                // shortening and leaves the completion request retained.
                if (revokeCleanedOwner)
                    throw new InvalidOperationException("Finalizer recovery refused its exact cleaned original owner.");
                return null;
            }
            owner = recovery;
        }
        var command = Command(resources, """
            UPDATE background_jobs j SET phase=@nextPhase,terminal_reason=@reason,
                available_after=clock_timestamp()+@delay,last_error=@error,probe_after=NULL,
                ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL WHERE
            """ + " " + ExternalFinalizeFence + " " + """
                AND ((@unowned AND j.phase='finalize_pending' AND j.finalize_attempts=6
                        AND j.ownership_token IS NULL AND j.available_after<=clock_timestamp())
                    OR (NOT @unowned AND j.phase='reconciling' AND j.ownership_kind='recovery'
                        AND j.ownership_until>clock_timestamp()))
            RETURNING j.id,j.wake_generation,j.phase,j.terminal_reason,j.available_after
            """);
        AddFinalizerFence(command, origin, owner);
        command.Parameters.AddWithValue("nextPhase", origin.ExpectedPhase!);
        command.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("delay", reason is null
            ? BackgroundJobRuntimeOptions.RetryDelays[row.FinalizeAttempts - 1] : TimeSpan.Zero);
        command.Parameters.AddWithValue("error", reason ?? errorCode);
        command.Parameters.AddWithValue("unowned", noOwnerExhaustion);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        (BackgroundJobControlChange, BackgroundJobCommitProbe)? result = null;
        if (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
        {
            var returned = resources.Reader;
            var change = new BackgroundJobControlChange(returned.GetGuid(0), returned.GetInt64(1), returned.GetString(2),
                returned.IsDBNull(3) ? null : returned.GetString(3));
            if (change.JobId != origin.JobId || change.WakeGeneration != origin.ExpectedGeneration
                || change.Phase != origin.ExpectedPhase || change.TerminalReason != origin.ExpectedTerminalReason)
                throw new InvalidOperationException("Finalizer control returned contradictory status.");
            result = (change, origin with { ExpectedAvailableAfter = returned.GetFieldValue<DateTimeOffset>(4) });
        }
        await FinishAsync(resources).ConfigureAwait(false);
        // A batch may contain other successful rows. Do not allow its later
        // COMMIT to retain a partial private acquisition for this row.
        if (result is null && !noOwnerExhaustion)
            throw new InvalidOperationException("Finalizer disposition refused after its private acquisition.");
        return result;
    }

    private const string ExternalFinalizeFence = """
        j.id=@id AND j.tenant_id=@tenant AND j.organization_id IS NOT DISTINCT FROM @organization
        AND j.definition_key=@definition AND j.queue=@queue AND j.capacity_group=@capacity
        AND j.original_due_at=@due AND j.profile='external' AND j.current_attempt_id=@attempt
        AND j.normal_attempts=@ordinal AND j.finalize_attempts=@finalOrdinal
        AND j.final_outcome_attempt_id=@selected AND j.finalization_status=@status AND j.finalization_reason=@moduleReason
        AND j.plan_fingerprint=@fingerprint AND j.operation_id=@operation AND j.prepared_at=@prepared
        AND NOT j.business_finalization_incomplete AND j.attempt_deadline=@deadline
        AND j.wake_generation=@generation AND j.ownership_token IS NOT DISTINCT FROM @token
        AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL AND j.terminal_reason IS NULL
        AND EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.id=@attempt AND a.job_id=j.id
            AND a.tenant_id=j.tenant_id AND a.organization_id IS NOT DISTINCT FROM j.organization_id
            AND a.stage='finalize' AND a.ordinal=@finalOrdinal AND a.admission_token=@admission
            AND a.admitted_at=@admitted AND a.deadline=@deadline AND a.final_outcome_attempt_id=@selected)
        """;

    private static void AddFinalizerFence(NpgsqlCommand command, BackgroundJobCommitProbe origin, Guid? owner)
    {
        var history = origin.FinalizationHistory!;
        command.Parameters.AddWithValue("id", origin.JobId);
        command.Parameters.AddWithValue("tenant", origin.TenantId);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)origin.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("definition", origin.DefinitionKey);
        command.Parameters.AddWithValue("queue", origin.Queue);
        command.Parameters.AddWithValue("capacity", origin.CapacityGroup!);
        command.Parameters.AddWithValue("due", origin.OriginalDueAt!.Value);
        command.Parameters.AddWithValue("attempt", origin.AttemptId!.Value);
        command.Parameters.AddWithValue("ordinal", origin.Ordinal!.Value);
        command.Parameters.AddWithValue("finalOrdinal", origin.FinalizeOrdinal!.Value);
        command.Parameters.AddWithValue("selected", origin.SelectedNormalAttemptId!.Value);
        command.Parameters.AddWithValue("deadline", origin.Deadline!.Value);
        command.Parameters.AddWithValue("generation", origin.WakeGeneration);
        command.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, (object?)owner ?? DBNull.Value);
        command.Parameters.AddWithValue("admission", origin.OriginalAdmissionToken!.Value);
        command.Parameters.AddWithValue("admitted", origin.AdmittedAt!.Value);
        command.Parameters.AddWithValue("status", history.Finalize.FinalizationStatus!);
        command.Parameters.AddWithValue("moduleReason", history.Finalize.FinalizationReason!);
        command.Parameters.AddWithValue("fingerprint", history.Finalize.PlanFingerprint!);
        command.Parameters.AddWithValue("operation", history.Finalize.OperationId!.Value);
        command.Parameters.AddWithValue("prepared", history.Finalize.PreparedAt!.Value);
    }

    private static BackgroundJobCommitProbe FinalizerProbe(ControlRow row, BackgroundJobFinalizeControlHistory history, string? reason) =>
        new(BackgroundJobCommitBoundary.ExternalFinalizeDisposition, row.JobId, row.TenantId, row.OrganizationId,
            row.DefinitionKey, row.Queue, row.WakeGeneration, row.AttemptId, row.OwnershipToken ?? Guid.Empty,
            ExpectedGeneration: reason is null ? checked(row.WakeGeneration + 1) : row.WakeGeneration,
            ExpectedPhase: reason is null ? "finalize_pending" : "terminal_pending", CapacityGroup: row.CapacityGroup,
            Ordinal: row.NormalAttempts, Deadline: row.Deadline, AdmittedAt: row.LedgerAdmittedAt,
            OriginalDueAt: row.OriginalDueAt, Profile: "external", LedgerStage: BackgroundJobLedgerStage.Finalize,
            FinalizeOrdinal: row.FinalizeAttempts, SelectedNormalAttemptId: row.SelectedNormalAttemptId,
            IsRecovery: row.OwnershipKind == "recovery", OriginalAdmissionToken: row.LedgerToken,
            ExpectedTerminalReason: reason)
        { FinalizationHistory = history };

    private async Task<BackgroundJobFinalizeControlHistory?> ReadFinalizerControlHistoryAsync(
        BackgroundJobControlResources resources, ControlRow row, CancellationToken token)
    {
        if (row.Profile != "external" || row.CapacityGroup is null || row.LedgerStage != "finalize"
            || !row.MatchingAttempt || row.NormalAttempts is < 1 or > 6 || row.FinalizeAttempts is < 1 or > 6
            || row.AttemptId is null || row.LedgerAdmittedAt is null) return null;
        var command = Command(resources, CheckpointHistoryProjection);
        command.Parameters.AddWithValue("id", row.JobId);
        command.Parameters.AddWithValue("attempt", row.AttemptId.Value);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        var final = await resources.Reader!.ReadAsync(token).ConfigureAwait(false) ? CaptureSelectedNormalHistory(resources.Reader) : null;
        await FinishAsync(resources).ConfigureAwait(false);
        if (final is null || !ValidHistoryIdentity(final) || final.CurrentAttemptId != final.AttemptId
            || final.FinalizeAttempts != final.Ordinal || final.Deadline != final.CurrentAttemptDeadline
            || await ReadSelectedFinalizationAsync(resources, final, token).ConfigureAwait(false) is not { } selected) return null;

        // Every normal admission must have positively ended and released its
        // own reservation. No elapsed lease stands in for an external fact.
        command = Command(resources, CheckpointHistoryColumns + " WHERE j.id=@id AND a.stage='normal' ORDER BY a.ordinal LIMIT 7");
        command.Parameters.AddWithValue("id", row.JobId);
        var prior = new List<BackgroundJobCheckpointHistoryFacts>();
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        while (await resources.Reader!.ReadAsync(token).ConfigureAwait(false)) prior.Add(CaptureSelectedNormalHistory(resources.Reader));
        await FinishAsync(resources).ConfigureAwait(false);
        if (prior.Count != row.NormalAttempts) return null;
        for (var i = 0; i < prior.Count; i++)
        {
            var normal = prior[i];
            if (!ValidHistoryIdentity(normal) || !ValidNormalHistory(normal) || normal.Ordinal != i + 1
                || normal.PlanFingerprint != final.PlanFingerprint || normal.OperationId != final.OperationId
                || normal.ReleasedAt is null || normal.NoStartAt is null && normal.OutcomeKind is null
                || i < prior.Count - 1 && !PositiveRetryHistory(normal)) return null;
        }
        if (prior[^1].AttemptId != row.SelectedNormalAttemptId) return null;
        return new(final, selected.Normal, selected.Decision);
    }

    private async Task<BackgroundJobProbeDisposition> ProbeExternalFinalizeAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, ControlRow? row, CancellationToken token)
    {
        if (origin.Boundary != BackgroundJobCommitBoundary.ExternalFinalizeDisposition
            || origin.Profile != "external" || origin.LedgerStage != BackgroundJobLedgerStage.Finalize
            || origin.Ordinal is not (>= 1 and <= 6) || origin.FinalizeOrdinal is not (>= 1 and <= 6)
            || origin.FinalizationHistory is not { } original || origin.ExpectedAvailableAfter is null
            || origin.Token != (original.Finalize.CurrentOwnershipToken ?? Guid.Empty)
            || origin.IsRecovery != (original.Finalize.CurrentOwnershipKind == "recovery")
            || origin.ExpectedTerminalReason is not (null or "finalize_refused" or "finalize_exhausted")
            || origin.ExpectedTerminalReason == "finalize_exhausted" && origin.FinalizeOrdinal != 6
            || origin.ExpectedTerminalReason is null && origin.FinalizeOrdinal == 6
            || origin.ExpectedPhase != (origin.ExpectedTerminalReason is null ? "finalize_pending" : "terminal_pending")
            || origin.ExpectedGeneration != origin.WakeGeneration + (origin.ExpectedTerminalReason is null ? 1 : 0))
            return BackgroundJobProbeDisposition.Unresolved;
        if (row is null) return BackgroundJobProbeDisposition.Unresolved;
        if (row.JobId != origin.JobId || row.TenantId != origin.TenantId || row.OrganizationId != origin.OrganizationId
            || row.DefinitionKey != origin.DefinitionKey || row.Queue != origin.Queue || row.CapacityGroup != origin.CapacityGroup
            || row.Profile != origin.Profile || row.OriginalDueAt != origin.OriginalDueAt || row.AttemptId != origin.AttemptId
            || row.NormalAttempts != origin.Ordinal || row.FinalizeAttempts != origin.FinalizeOrdinal
            || row.SelectedNormalAttemptId != origin.SelectedNormalAttemptId || row.Deadline != origin.Deadline
            || row.LedgerToken != origin.OriginalAdmissionToken || row.LedgerAdmittedAt != origin.AdmittedAt || !row.MatchingAttempt)
            return BackgroundJobProbeDisposition.ChangedOwner;
        if (await ReadFinalizerControlHistoryAsync(resources, row, token).ConfigureAwait(false) is not { } history
            || !SameFinalizerHistory(original, history)) return BackgroundJobProbeDisposition.Unresolved;
        if (row.WakeGeneration == origin.WakeGeneration && row.Phase == original.Finalize.Phase
            && row.OwnershipToken == original.Finalize.CurrentOwnershipToken
            && row.OwnershipKind == original.Finalize.CurrentOwnershipKind && row.Incomplete)
            return BackgroundJobProbeDisposition.RolledBack;
        if (row.WakeGeneration != origin.ExpectedGeneration || row.Phase != origin.ExpectedPhase
            || row.TerminalReason != origin.ExpectedTerminalReason) return BackgroundJobProbeDisposition.ChangedOwner;
        if (row.ProcessedAt is not null) return BackgroundJobProbeDisposition.ChangedOwner;
        if (origin.ExpectedTerminalReason is null)
        {
            if (row.DeadLetteredAt is not null || history.Finalize.BusinessFinalizationIncomplete
                || !(row.OwnershipToken is null && row.OwnershipKind is null && row.OwnershipUntil is null
                    || row.OwnershipToken is { } owner && owner != Guid.Empty && row.OwnershipKind == "routing"
                        && row.OwnershipUntil is not null)) return BackgroundJobProbeDisposition.ChangedOwner;
        }
        else if (row.DeadLetteredAt is { } completed)
        {
            // A later exact bypass terminal commit proves its reason-bound
            // request too. Ordinary app completion is never this confirmation.
            return history.Finalize.BusinessFinalizationIncomplete && completed <= history.Finalize.DbNow
                ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.Unresolved;
        }
        else if (history.Finalize.BusinessFinalizationIncomplete
            || !(row.OwnershipToken is null && row.OwnershipKind is null && row.OwnershipUntil is null
                || row.OwnershipToken is { } owner && owner != Guid.Empty && row.OwnershipKind == "recovery"
                    && row.OwnershipUntil is not null)) return BackgroundJobProbeDisposition.ChangedOwner;
        var command = Command(resources, "SELECT available_after FROM background_jobs WHERE id=@id");
        command.Parameters.AddWithValue("id", origin.JobId);
        var available = (DateTime)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        return new DateTimeOffset(available) == origin.ExpectedAvailableAfter
            ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.ChangedOwner;
    }

    private static bool SameFinalizerHistory(BackgroundJobFinalizeControlHistory original,
        BackgroundJobFinalizeControlHistory current) =>
        original.Finalize.JobId == current.Finalize.JobId && original.Finalize.TenantId == current.Finalize.TenantId
        && original.Finalize.OrganizationId == current.Finalize.OrganizationId
        && original.Finalize.DefinitionKey == current.Finalize.DefinitionKey && original.Finalize.Queue == current.Finalize.Queue
        && original.Finalize.CapacityGroup == current.Finalize.CapacityGroup
        && original.Finalize.OriginalDueAt == current.Finalize.OriginalDueAt
        && original.Finalize.AttemptId == current.Finalize.AttemptId && original.Finalize.Ordinal == current.Finalize.Ordinal
        && original.Finalize.AdmissionToken == current.Finalize.AdmissionToken
        && original.Finalize.AdmittedAt == current.Finalize.AdmittedAt && original.Finalize.Deadline == current.Finalize.Deadline
        && original.Finalize.NormalAttempts == current.Finalize.NormalAttempts
        && original.Finalize.FinalizeAttempts == current.Finalize.FinalizeAttempts
        && original.Finalize.SelectedNormalAttemptId == current.Finalize.SelectedNormalAttemptId
        && original.Finalize.JobSelectedNormalAttemptId == current.Finalize.JobSelectedNormalAttemptId
        && original.Finalize.PlanFingerprint == current.Finalize.PlanFingerprint
        && original.Finalize.OperationId == current.Finalize.OperationId && original.Finalize.PreparedAt == current.Finalize.PreparedAt
        && original.Finalize.FinalizationStatus == current.Finalize.FinalizationStatus
        && original.Finalize.FinalizationReason == current.Finalize.FinalizationReason
        && original.Selected.AttemptId == current.Selected.AttemptId && original.Selected.Ordinal == current.Selected.Ordinal
        && original.Selected.AdmissionToken == current.Selected.AdmissionToken
        && original.Selected.AdmittedAt == current.Selected.AdmittedAt && original.Selected.Deadline == current.Selected.Deadline
        && original.Selected.AttemptPlanFingerprint == current.Selected.AttemptPlanFingerprint
        && original.Selected.AttemptOperationId == current.Selected.AttemptOperationId
        && original.Selected.ExecuteIntentAt == current.Selected.ExecuteIntentAt
        && original.Selected.OutcomeKind == current.Selected.OutcomeKind && original.Selected.OutcomeAt == current.Selected.OutcomeAt
        && original.Selected.FailurePolicy == current.Selected.FailurePolicy && original.Selected.NoStartAt == current.Selected.NoStartAt
        && original.Selected.QuiescenceId == current.Selected.QuiescenceId
        && original.Selected.QuiescenceKind == current.Selected.QuiescenceKind && original.Selected.QuiescentAt == current.Selected.QuiescentAt
        && original.Selected.ReservationAttemptId == current.Selected.ReservationAttemptId
        && original.Selected.ReleasedAt == current.Selected.ReleasedAt && original.Selected.ReleaseReason == current.Selected.ReleaseReason
        && original.Selection == current.Selection;
}

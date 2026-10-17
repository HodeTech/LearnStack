using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Original locked metadata only; never a receipt or permission to execute.</summary>
internal sealed class BackgroundJobUninvokableRequestHistory
{
    internal BackgroundJobUninvokableRequestHistory(NpgsqlDataReader row, string reason)
    {
        if (row.FieldCount != 37) throw new InvalidOperationException("Uninvokable history requires its exact metadata projection.");
        OwnershipToken = GuidAt(row, 7);
        Phase = row.GetString(21);
        OwnershipKind = TextAt(row, 22);
        PlanFingerprint = TextAt(row, 25);
        OperationId = GuidAt(row, 26);
        PreparedAt = TimeAt(row, 27);
        FinalizationStatus = TextAt(row, 28);
        FinalizationReason = TextAt(row, 29);
        BusinessFinalizationIncomplete = row.GetBoolean(30);
        ProcessedAt = TimeAt(row, 31);
        DeadLetteredAt = TimeAt(row, 32);
        TerminalReason = TextAt(row, 33);
        DbNow = row.GetFieldValue<DateTimeOffset>(35);
        SelectedFacts = TextAt(row, 36);
        LedgerStage = TextAt(row, 17);
        LedgerOrdinal = row.IsDBNull(18) ? null : row.GetInt32(18);
        LedgerDeadline = TimeAt(row, 19);
        LedgerSelectedNormalAttemptId = GuidAt(row, 20);
        Original = new(BackgroundJobCommitBoundary.ExternalUninvokableTerminalRequest,
            row.GetGuid(0), row.GetGuid(1), GuidAt(row, 2), row.GetString(3), row.GetString(4),
            row.GetInt64(5), GuidAt(row, 6), OwnershipToken ?? Guid.Empty,
            ExpectedGeneration: row.GetInt64(5), ExpectedPhase: "terminal_pending", ExpectedUntil: TimeAt(row, 8),
            CapacityGroup: TextAt(row, 9), Ordinal: row.GetInt32(11), Deadline: TimeAt(row, 14),
            AdmittedAt: TimeAt(row, 16), OriginalDueAt: row.GetFieldValue<DateTimeOffset>(10), Profile: row.GetString(34),
            LedgerStage: LedgerStage switch { "normal" => BackgroundJobLedgerStage.Normal, "finalize" => BackgroundJobLedgerStage.Finalize, _ => null },
            FinalizeOrdinal: row.GetInt32(12), SelectedNormalAttemptId: GuidAt(row, 13),
            IsRecovery: OwnershipKind == "recovery", OriginalAdmissionToken: GuidAt(row, 15),
            ExpectedAvailableAfter: row.GetFieldValue<DateTimeOffset>(23), ExpectedProbeAfter: TimeAt(row, 24),
            ExpectedTerminalReason: reason);
    }

    internal BackgroundJobCommitProbe Original { get; }
    internal Guid? OwnershipToken { get; }
    internal string Phase { get; }
    internal string? OwnershipKind { get; }
    internal string? LedgerStage { get; }
    internal int? LedgerOrdinal { get; }
    internal DateTimeOffset? LedgerDeadline { get; }
    internal Guid? LedgerSelectedNormalAttemptId { get; }
    internal string? PlanFingerprint { get; }
    internal Guid? OperationId { get; }
    internal DateTimeOffset? PreparedAt { get; }
    internal string? FinalizationStatus { get; }
    internal string? FinalizationReason { get; }
    internal bool BusinessFinalizationIncomplete { get; }
    internal DateTimeOffset? ProcessedAt { get; }
    internal DateTimeOffset? DeadLetteredAt { get; }
    internal string? TerminalReason { get; }
    internal DateTimeOffset DbNow { get; }
    // The selected normal's immutable metadata, not its provider payload.
    // SQL serializes timestamps as exact epoch numerics, independent of TimeZone.
    internal string? SelectedFacts { get; }

    internal BackgroundJobCommitProbe Probe() => Original with { UninvokableHistory = this };
    internal bool Matches(BackgroundJobCommitProbe probe) => ReferenceEquals(probe.UninvokableHistory, this)
        && (probe with { UninvokableHistory = null }) == Original;

    private static Guid? GuidAt(NpgsqlDataReader row, int index) => row.IsDBNull(index) ? null : row.GetGuid(index);
    private static string? TextAt(NpgsqlDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    private static DateTimeOffset? TimeAt(NpgsqlDataReader row, int index) => row.IsDBNull(index) ? null : row.GetFieldValue<DateTimeOffset>(index);
}

internal sealed partial class PostgresBackgroundJobControlStore
{
    // Positive history precedes LIMIT so unresolved retired work cannot hide
    // later eligible jobs. The projected reason is the exact tombstone reason.
    private static string RetirementSelectionSql() => $"""
        SELECT candidate.id FROM background_jobs candidate
        WHERE {Incomplete} AND NOT (candidate.id=ANY(@blocked)) AND candidate.definition_key=ANY(@retired)
            AND candidate.available_after<=statement_timestamp()
            AND ((candidate.profile='transactional' AND candidate.ownership_token IS NULL
                    AND candidate.phase='pending' AND candidate.normal_attempts<6)
                OR (candidate.profile='external' AND candidate.original_due_at<=statement_timestamp()
                    AND ((candidate.ownership_token IS NULL AND candidate.ownership_kind IS NULL AND candidate.ownership_until IS NULL
                            AND candidate.phase IN ('pending','finalize_pending','reconciling'))
                        OR (candidate.ownership_token IS NOT NULL AND candidate.ownership_until<=statement_timestamp()
                            AND ((candidate.ownership_kind='routing' AND candidate.phase IN ('pending','finalize_pending'))
                                OR (candidate.ownership_kind='execution' AND candidate.phase IN ('preparing','prepared','executing','finalizing'))
                                OR (candidate.ownership_kind='recovery' AND candidate.phase='reconciling'))))
                    AND EXISTS (SELECT 1 FROM (
                        SELECT {UninvokableRequestedColumns} 'retired_definition'::text AS terminal_reason
                        FROM background_jobs j WHERE j.id=candidate.id) j
                        WHERE ({UninvokablePendingHistory}) AND ({UninvokableHistoryChronology}))))
        ORDER BY candidate.available_after,candidate.created_at,candidate.id LIMIT @batch FOR UPDATE SKIP LOCKED
        """;

    private async Task<(BackgroundJobControlChange Change, BackgroundJobCommitProbe Probe)?> RequestExternalUninvokableAsync(
        BackgroundJobControlResources resources, ControlRow row, BackgroundJobTerminalReason reason,
        BackgroundJobRoutingClaim? invalidClaim, CancellationToken token)
    {
        if (row.Profile != "external" || !row.Incomplete
            || reason is not (BackgroundJobTerminalReason.RetiredDefinition or BackgroundJobTerminalReason.InvalidEnvelope)
            || (reason == BackgroundJobTerminalReason.RetiredDefinition
                ? invalidClaim is not null || definitions.Resolve(row.DefinitionKey).Disposition != BackgroundJobDefinitionDisposition.Retired
                : invalidClaim is null || !row.Matches(invalidClaim))) return null;

        // The caller already holds the actual job lock. A fresh statement also
        // observes ledger/selection facts committed by any prior lock holder.
        var history = await ReadUninvokableRequestHistoryAsync(resources, row.JobId,
            BackgroundJobFailurePolicy.TerminalCode(reason), token).ConfigureAwait(false);
        if (history is null || !ValidUninvokableHeader(history) || !UninvokableOriginalOwner(history, invalidClaim)
            || !await ReadUninvokableEligibilityAsync(resources, history, completed: false, token).ConfigureAwait(false)) return null;
        var origin = history.Probe();
        var command = Command(resources, """
            UPDATE background_jobs j SET phase='terminal_pending',terminal_reason=@reason,
                available_after=clock_timestamp(),probe_after=NULL,
                ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL
            WHERE j.id=@id AND j.tenant_id=@tenant AND j.organization_id IS NOT DISTINCT FROM @organization
                AND j.definition_key=@definition AND j.queue=@queue AND j.capacity_group IS NOT DISTINCT FROM @capacity
                AND j.profile='external' AND j.original_due_at=@due AND j.wake_generation=@generation
                AND j.current_attempt_id IS NOT DISTINCT FROM @attempt AND j.attempt_deadline IS NOT DISTINCT FROM @deadline
                AND j.normal_attempts=@ordinal AND j.finalize_attempts=@finalOrdinal
                AND j.final_outcome_attempt_id IS NOT DISTINCT FROM @selected
                AND j.phase=@phase AND j.ownership_token IS NOT DISTINCT FROM @token
                AND j.ownership_kind IS NOT DISTINCT FROM @kind AND j.ownership_until IS NOT DISTINCT FROM @until
                AND j.available_after=@available AND j.probe_after IS NOT DISTINCT FROM @probeAfter
                AND j.plan_fingerprint IS NOT DISTINCT FROM @fingerprint AND j.operation_id IS NOT DISTINCT FROM @operation
                AND j.prepared_at IS NOT DISTINCT FROM @prepared
                AND j.finalization_status IS NOT DISTINCT FROM @status AND j.finalization_reason IS NOT DISTINCT FROM @moduleReason
                AND NOT j.business_finalization_incomplete
                AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL AND j.terminal_reason IS NULL
                AND ((@invalid AND j.ownership_kind='routing' AND j.ownership_until>clock_timestamp())
                    OR (NOT @invalid AND (j.ownership_token IS NULL OR j.ownership_until<=clock_timestamp())))
            RETURNING j.id,j.wake_generation,j.phase,j.terminal_reason
            """);
        AddUninvokableFence(command, history);
        command.Parameters.AddWithValue("invalid", invalidClaim is not null);
        // The full scoped history was proved above under this same held lock;
        // this write changes only terminal scheduling and relinquishes ownership.
        var changed = await ReadChangeAsync(resources, token).ConfigureAwait(false);
        if (changed is null) return null;
        if (changed.JobId != origin.JobId || changed.WakeGeneration != origin.WakeGeneration
            || changed.Phase != origin.ExpectedPhase || changed.TerminalReason != origin.ExpectedTerminalReason)
            throw new InvalidOperationException("Uninvokable request returned contradictory metadata.");
        return (changed, origin);
    }

    private static bool ValidUninvokableHeader(BackgroundJobUninvokableRequestHistory history)
    {
        var origin = history.Original;
        if (origin.Profile != "external" || origin.JobId.Version != 7 || origin.TenantId == Guid.Empty
            || origin.TenantId == TenantId.PlatformSentinel.Value || origin.OrganizationId == Guid.Empty
            || !BackgroundJobNames.Definition(origin.DefinitionKey) || !BackgroundJobNames.QueueOrPool(origin.Queue)
            || origin.CapacityGroup is not null && !BackgroundJobNames.ModuleResource(origin.CapacityGroup)
            || origin.WakeGeneration < 1 || origin.Ordinal is not (>= 0 and <= 6)
            || origin.FinalizeOrdinal is not (>= 0 and <= 6) || origin.OriginalDueAt > history.DbNow
            || origin.ExpectedTerminalReason is not ("retired_definition" or "invalid_envelope")) return false;
        if (origin.Ordinal == 0)
            return origin.AttemptId is null && origin.Deadline is null && origin.OriginalAdmissionToken is null
                && origin.AdmittedAt is null && history.LedgerStage is null && history.LedgerOrdinal is null
                && history.LedgerDeadline is null && history.LedgerSelectedNormalAttemptId is null;
        return origin.CapacityGroup is not null && origin.AttemptId?.Version == 7
            && origin.OriginalAdmissionToken is { } admission && admission != Guid.Empty
            && origin.AdmittedAt >= origin.OriginalDueAt && origin.AdmittedAt <= history.DbNow
            && origin.Deadline > origin.AdmittedAt && origin.Deadline - origin.AdmittedAt <= TimeSpan.FromHours(24)
            && history.LedgerDeadline == origin.Deadline
            && (origin.FinalizeOrdinal == 0
                ? history.LedgerStage == "normal" && history.LedgerOrdinal == origin.Ordinal && history.LedgerSelectedNormalAttemptId is null
                : history.LedgerStage == "finalize" && history.LedgerOrdinal == origin.FinalizeOrdinal
                    && origin.SelectedNormalAttemptId is not null && history.LedgerSelectedNormalAttemptId == origin.SelectedNormalAttemptId);
    }

    private static bool UninvokableOriginalOwner(BackgroundJobUninvokableRequestHistory history, BackgroundJobRoutingClaim? invalidClaim)
    {
        var origin = history.Original;
        if (history.ProcessedAt is not null || history.DeadLetteredAt is not null || history.TerminalReason is not null
            || history.BusinessFinalizationIncomplete || origin.ExpectedAvailableAfter > history.DbNow
            || !UninvokableOriginalPhase(history)) return false;
        if (invalidClaim is not null)
            return origin.ExpectedTerminalReason == "invalid_envelope" && invalidClaim.Profile == BackgroundJobExecutionProfile.External
                && origin.JobId == invalidClaim.JobId.Value && origin.TenantId == invalidClaim.TenantId.Value
                && origin.OrganizationId == invalidClaim.OrganizationId?.Value && origin.DefinitionKey == invalidClaim.DefinitionKey
                && origin.Queue == invalidClaim.Queue && origin.WakeGeneration == invalidClaim.WakeGeneration
                && origin.OriginalDueAt == invalidClaim.OriginalDueAt && history.OwnershipToken == invalidClaim.Token
                && origin.ExpectedUntil == invalidClaim.OwnershipUntil && origin.ExpectedUntil > history.DbNow
                && history.OwnershipKind == "routing"
                && (history.Phase == "pending" && invalidClaim.LedgerStage == BackgroundJobLedgerStage.Normal && origin.FinalizeOrdinal == 0
                    || history.Phase == "finalize_pending" && invalidClaim.LedgerStage == BackgroundJobLedgerStage.Finalize
                        && origin.SelectedNormalAttemptId is not null);
        if (origin.ExpectedTerminalReason != "retired_definition") return false;
        if (history.OwnershipToken is null)
            return history.OwnershipKind is null && origin.ExpectedUntil is null
                && history.Phase is "pending" or "finalize_pending" or "reconciling";
        return history.OwnershipToken != Guid.Empty && origin.ExpectedUntil <= history.DbNow
            && (history.OwnershipKind == "routing" && history.Phase is "pending" or "finalize_pending"
                || history.OwnershipKind == "execution" && history.OwnershipToken == origin.OriginalAdmissionToken
                    && history.Phase is "preparing" or "prepared" or "executing" or "finalizing"
                || history.OwnershipKind == "recovery" && history.OwnershipToken != origin.OriginalAdmissionToken && history.Phase == "reconciling");
    }

    private static bool UninvokableOriginalPhase(BackgroundJobUninvokableRequestHistory history) => history.Phase switch
    {
        "pending" => history.Original.FinalizeOrdinal == 0 && history.Original.SelectedNormalAttemptId is null,
        "preparing" => history.Original.Ordinal > 0 && history.Original.FinalizeOrdinal == 0
            && history.Original.SelectedNormalAttemptId is null,
        "prepared" or "executing" => history.Original.Ordinal > 0 && history.Original.FinalizeOrdinal == 0
            && history.Original.SelectedNormalAttemptId is null && history.PlanFingerprint is not null,
        "finalize_pending" => history.Original.SelectedNormalAttemptId is not null && history.PlanFingerprint is not null,
        "finalizing" => history.Original.FinalizeOrdinal > 0 && history.Original.SelectedNormalAttemptId is not null
            && history.PlanFingerprint is not null,
        "reconciling" => history.Original.Ordinal > 0,
        _ => false,
    };

    private async Task<BackgroundJobUninvokableRequestHistory?> ReadUninvokableRequestHistoryAsync(
        BackgroundJobControlResources resources, Guid jobId, string reason, CancellationToken token)
    {
        var command = Command(resources, """
            SELECT j.id,j.tenant_id,j.organization_id,j.definition_key,j.queue,j.wake_generation,
                j.current_attempt_id,j.ownership_token,j.ownership_until,j.capacity_group,j.original_due_at,
                j.normal_attempts,j.finalize_attempts,j.final_outcome_attempt_id,j.attempt_deadline,
                a.admission_token,a.admitted_at,a.stage,a.ordinal,a.deadline,a.final_outcome_attempt_id,
                j.phase,j.ownership_kind,j.available_after,j.probe_after,j.plan_fingerprint,j.operation_id,j.prepared_at,
                j.finalization_status,j.finalization_reason,j.business_finalization_incomplete,j.processed_at,j.dead_lettered_at,
                j.terminal_reason,j.profile,clock_timestamp(),
                CASE WHEN s.id IS NULL THEN NULL ELSE jsonb_build_array(s.id,s.tenant_id,s.organization_id,s.job_id,
                    s.stage,s.ordinal,s.admission_token,extract(epoch FROM s.admitted_at),extract(epoch FROM s.deadline),s.final_outcome_attempt_id,
                    s.plan_fingerprint,s.operation_id,extract(epoch FROM s.execute_intent_at),s.outcome_kind,extract(epoch FROM s.outcome_at),s.failure_policy,
                    s.quiescence_id,s.quiescence_kind,extract(epoch FROM s.quiescent_at),extract(epoch FROM s.no_start_at),extract(epoch FROM s.transactional_cleanup_at))::text END
            FROM background_jobs j
            LEFT JOIN background_job_attempts a ON a.id=j.current_attempt_id AND a.tenant_id=j.tenant_id AND a.job_id=j.id
                AND a.organization_id IS NOT DISTINCT FROM j.organization_id
            LEFT JOIN background_job_attempts s ON s.id=j.final_outcome_attempt_id AND s.tenant_id=j.tenant_id AND s.job_id=j.id
                AND s.organization_id IS NOT DISTINCT FROM j.organization_id
            WHERE j.id=@id
            """);
        command.Parameters.AddWithValue("id", jobId);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        var history = await resources.Reader!.ReadAsync(token).ConfigureAwait(false)
            ? new BackgroundJobUninvokableRequestHistory(resources.Reader, reason) : null;
        await FinishAsync(resources).ConfigureAwait(false);
        return history;
    }

    // Reuse the strict terminal predicates without changing their phase/reason
    // contract: only these two projected routing fields represent the requested
    // state. Every history, scope, count and immutable plan fact is still actual.
    private const string UninvokableRequestedColumns = """
        j.id,j.tenant_id,j.organization_id,j.profile,j.capacity_group,j.original_due_at,j.wake_generation,
        j.normal_attempts,j.finalize_attempts,j.current_attempt_id,j.attempt_deadline,
        j.plan_fingerprint,j.operation_id,j.prepared_at,j.final_outcome_attempt_id,
        j.finalization_status,j.finalization_reason,j.business_finalization_incomplete,
        j.processed_at,j.dead_lettered_at,'terminal_pending'::text AS phase,
        """;

    private const string UninvokablePendingHistory =
        "j.processed_at IS NULL AND j.dead_lettered_at IS NULL AND NOT j.business_finalization_incomplete AND ((j.normal_attempts=0 AND ("
        + BackgroundJobTerminalEligibility.NoPlan + ")) OR (" + BackgroundJobTerminalEligibility.NoPlanCleanupHistory
        + ") OR (" + BackgroundJobTerminalEligibility.PreparedCleanupHistory + ") OR ("
        + BackgroundJobTerminalEligibility.PreparedHistory + "))";

    private const string UninvokableHistoryChronology =
        "NOT EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id"
        + " AND a.admitted_at<j.original_due_at)";

    private async Task<bool> ReadUninvokableEligibilityAsync(BackgroundJobControlResources resources,
        BackgroundJobUninvokableRequestHistory history, bool completed, CancellationToken token)
    {
        var eligibility = completed
            ? "((" + BackgroundJobTerminalEligibility.NoPlan + ") AND (j.normal_attempts=0 OR ("
                + BackgroundJobTerminalEligibility.NoPlanCleanupHistory + ")) AND j.processed_at IS NULL"
                + " AND j.dead_lettered_at IS NOT NULL AND isfinite(j.dead_lettered_at)"
                + " AND j.dead_lettered_at>=j.original_due_at AND j.dead_lettered_at<=clock_timestamp()"
                + " AND NOT EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id"
                + " AND (a.admitted_at>j.dead_lettered_at OR a.no_start_at>j.dead_lettered_at))"
                + " AND NOT EXISTS (SELECT 1 FROM background_job_reservations r WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id"
                + " AND r.released_at>j.dead_lettered_at)) OR (" + BackgroundJobTerminalEligibility.PreparedAfter + ")"
            : UninvokablePendingHistory;
        var command = Command(resources, $"""
            SELECT TRUE FROM (
                SELECT {UninvokableRequestedColumns} @reason::text AS terminal_reason
                FROM background_jobs j WHERE j.id=@id) j
            WHERE ({eligibility}) AND ({UninvokableHistoryChronology})
            """);
        command.Parameters.AddWithValue("id", history.Original.JobId);
        command.Parameters.AddWithValue("reason", history.Original.ExpectedTerminalReason!);
        var valid = await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        return valid;
    }

    private static void AddUninvokableFence(NpgsqlCommand command, BackgroundJobUninvokableRequestHistory history)
    {
        var origin = history.Original;
        command.Parameters.AddWithValue("id", origin.JobId);
        command.Parameters.AddWithValue("tenant", origin.TenantId);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)origin.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("definition", origin.DefinitionKey);
        command.Parameters.AddWithValue("queue", origin.Queue);
        command.Parameters.AddWithValue("capacity", NpgsqlDbType.Text, (object?)origin.CapacityGroup ?? DBNull.Value);
        command.Parameters.AddWithValue("due", origin.OriginalDueAt!.Value);
        command.Parameters.AddWithValue("generation", origin.WakeGeneration);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, (object?)origin.AttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue("deadline", NpgsqlDbType.TimestampTz, (object?)origin.Deadline ?? DBNull.Value);
        command.Parameters.AddWithValue("ordinal", origin.Ordinal!.Value);
        command.Parameters.AddWithValue("finalOrdinal", origin.FinalizeOrdinal!.Value);
        command.Parameters.AddWithValue("selected", NpgsqlDbType.Uuid, (object?)origin.SelectedNormalAttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue("phase", history.Phase);
        command.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, (object?)history.OwnershipToken ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Text, (object?)history.OwnershipKind ?? DBNull.Value);
        command.Parameters.AddWithValue("until", NpgsqlDbType.TimestampTz, (object?)origin.ExpectedUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("available", origin.ExpectedAvailableAfter!.Value);
        command.Parameters.AddWithValue("probeAfter", NpgsqlDbType.TimestampTz, (object?)origin.ExpectedProbeAfter ?? DBNull.Value);
        command.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Text, (object?)history.PlanFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, (object?)history.OperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("prepared", NpgsqlDbType.TimestampTz, (object?)history.PreparedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, (object?)history.FinalizationStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("moduleReason", NpgsqlDbType.Text, (object?)history.FinalizationReason ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", origin.ExpectedTerminalReason!);
    }

    private static bool ValidUninvokableOriginalHistory(BackgroundJobUninvokableRequestHistory history)
    {
        if (!ValidUninvokableHeader(history) || !UninvokableOriginalPhase(history)
            || history.ProcessedAt is not null || history.DeadLetteredAt is not null || history.TerminalReason is not null
            || history.BusinessFinalizationIncomplete || history.Original.ExpectedAvailableAfter > history.DbNow) return false;
        return history.Original.ExpectedTerminalReason == "retired_definition"
            ? UninvokableOriginalOwner(history, null)
            : history.OwnershipToken is { } token && token != Guid.Empty && history.OwnershipKind == "routing"
                && history.Original.ExpectedUntil > history.DbNow && history.Phase is "pending" or "finalize_pending";
    }

    private async Task<BackgroundJobProbeDisposition> ProbeExternalUninvokableRequestAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, ControlRow? row, CancellationToken token)
    {
        if (origin.UninvokableHistory is not { } original || !original.Matches(origin) || !ValidUninvokableOriginalHistory(original))
            return BackgroundJobProbeDisposition.Unresolved;
        if (row is null) return BackgroundJobProbeDisposition.Unresolved;
        if (row.JobId != origin.JobId || row.TenantId != origin.TenantId || row.OrganizationId != origin.OrganizationId
            || row.DefinitionKey != origin.DefinitionKey || row.Queue != origin.Queue || row.CapacityGroup != origin.CapacityGroup
            || row.Profile != "external" || row.OriginalDueAt != origin.OriginalDueAt || row.WakeGeneration != origin.WakeGeneration
            || row.AttemptId != origin.AttemptId || row.NormalAttempts != origin.Ordinal || row.FinalizeAttempts != origin.FinalizeOrdinal
            || row.SelectedNormalAttemptId != origin.SelectedNormalAttemptId)
            return BackgroundJobProbeDisposition.ChangedOwner;
        var current = await ReadUninvokableRequestHistoryAsync(resources, origin.JobId, origin.ExpectedTerminalReason!, token).ConfigureAwait(false);
        if (current is null || !ValidUninvokableHeader(current)
            || current.Original.Deadline != origin.Deadline || current.Original.AdmittedAt != origin.AdmittedAt
            || current.Original.OriginalAdmissionToken != origin.OriginalAdmissionToken || current.LedgerStage != original.LedgerStage
            || current.LedgerOrdinal != original.LedgerOrdinal || current.LedgerDeadline != original.LedgerDeadline
            || current.LedgerSelectedNormalAttemptId != original.LedgerSelectedNormalAttemptId
            || current.PlanFingerprint != original.PlanFingerprint || current.OperationId != original.OperationId
            || current.PreparedAt != original.PreparedAt || current.FinalizationStatus != original.FinalizationStatus
            || current.FinalizationReason != original.FinalizationReason || current.SelectedFacts != original.SelectedFacts)
            return BackgroundJobProbeDisposition.Unresolved;
        if (current.Phase == "terminal_pending" && current.TerminalReason == origin.ExpectedTerminalReason && current.ProcessedAt is null)
        {
            var noOwner = current.OwnershipToken is null && current.OwnershipKind is null && current.Original.ExpectedUntil is null;
            var laterRecovery = current.DeadLetteredAt is null && current.OwnershipToken is { } owner && owner != Guid.Empty
                && owner != original.OwnershipToken && owner != origin.OriginalAdmissionToken
                && current.OwnershipKind == "recovery" && current.Original.ExpectedUntil is not null;
            return (noOwner || laterRecovery)
                && await ReadUninvokableEligibilityAsync(resources, current, current.DeadLetteredAt is not null, token).ConfigureAwait(false)
                ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.Unresolved;
        }
        if (current.Phase == original.Phase && current.TerminalReason is null
            && current.OwnershipToken == original.OwnershipToken && current.OwnershipKind == original.OwnershipKind
            && current.Original.ExpectedUntil == origin.ExpectedUntil
            && current.Original.ExpectedAvailableAfter == origin.ExpectedAvailableAfter
            && current.Original.ExpectedProbeAfter == origin.ExpectedProbeAfter)
            return await ReadUninvokableEligibilityAsync(resources, current, completed: false, token).ConfigureAwait(false)
                ? BackgroundJobProbeDisposition.RolledBack : BackgroundJobProbeDisposition.Unresolved;
        return current.OwnershipToken != original.OwnershipToken || current.OwnershipKind != original.OwnershipKind
            ? BackgroundJobProbeDisposition.ChangedOwner : BackgroundJobProbeDisposition.Unresolved;
    }
}

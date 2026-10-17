using LearnStack.Application.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> DeferCapacityAsync(
        BackgroundJobRoutingClaim claim, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        BackgroundJobControlChange? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var row = await LockRowAsync(resources, claim.JobId.Value, deadline.Token).ConfigureAwait(false);
                if (row is not null && row.Matches(claim) && row.Incomplete && row.Profile == "transactional"
                    && row.CapacityGroup is not null && row.Phase == "pending" && row.OwnershipKind == "routing")
                {
                    var command = Command(resources, """
                        UPDATE background_jobs SET ownership_token=NULL, ownership_kind=NULL, ownership_until=NULL,
                            available_after=clock_timestamp()+@delay, handoff_check_at=clock_timestamp(), last_error='capacity_unavailable'
                        WHERE id=@id AND wake_generation=@generation AND ownership_token=@token AND ownership_kind='routing'
                            AND ownership_until>clock_timestamp() AND phase='pending'
                            AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
                        RETURNING id,wake_generation,phase,terminal_reason
                        """);
                    AddRowFence(command, row);
                    command.Parameters.AddWithValue("delay", options.RecoveryProbeInterval);
                    value = await ReadChangeAsync(resources, deadline.Token).ConfigureAwait(false);
                    if (value is not null) probes.Add(Probe(row, value));
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(resources.CommitAcknowledged ? value : null, resources, probes.AsReadOnly());
    }

    private const string RecoveryProjection = """
        SELECT j.id, j.tenant_id, j.organization_id, j.definition_key, j.profile, j.queue, j.capacity_group,
            j.original_due_at, a.id, a.ordinal, a.admitted_at, a.deadline, a.admission_token,
            j.wake_generation, j.ownership_token, j.ownership_until, clock_timestamp()
        FROM background_jobs j JOIN background_job_attempts a
            ON a.tenant_id=j.tenant_id AND a.job_id=j.id AND a.id=j.current_attempt_id
        WHERE j.id=@id AND j.phase='reconciling' AND j.ownership_kind='recovery'
            AND j.ownership_token=@recovery AND j.ownership_until>clock_timestamp()
        """;

    internal async Task<OwnedBackgroundJobControl<BackgroundJobCapacityRecoveryReceipt>> AcquireCapacityRecoveryAsync(
        BackgroundJobActualCleanup cleanup, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(cleanup);
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        Candidate? candidate = null;
        BackgroundJobCapacityRecoveryReceipt? receipt = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var row = await LockRowAsync(resources, cleanup.Envelope.JobId.Value, deadline.Token).ConfigureAwait(false);
                if (row is not null && MatchesCleanup(row, cleanup) && row.Incomplete && row.MatchingAttempt
                    && row.LedgerToken == cleanup.Claim.Token && row.Phase is "executing" or "reconciling")
                {
                    if (row.OwnershipToken == cleanup.Claim.Token && row.OwnershipKind == "execution")
                    {
                        var expire = Command(resources, """
                            UPDATE background_jobs SET ownership_until=clock_timestamp()
                            WHERE id=@id AND current_attempt_id=@attempt AND ownership_token=@token
                                AND wake_generation=@generation AND phase='executing' AND ownership_kind='execution'
                                AND ownership_until>clock_timestamp()
                            """);
                        AddRowFence(expire, row);
                        await expire.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                        await resources.FinishCommandAsync().ConfigureAwait(false);
                    }
                    var recovery = ids.NewUuidV4();
                    var acquired = await AcquireRecoveryTokenAsync(resources, row, recovery, deadline.Token).ConfigureAwait(false);
                    if (acquired)
                    {
                        candidate = await ReadRecoveryCandidateAsync(resources, row.JobId, recovery, deadline.Token).ConfigureAwait(false);
                        probes.Add(new(BackgroundJobCommitBoundary.CapacityRecovery, row.JobId, row.TenantId,
                            row.OrganizationId, row.DefinitionKey, row.Queue, row.WakeGeneration, row.AttemptId,
                            row.LedgerToken!.Value, CapacityGroup: row.CapacityGroup, RecoveryToken: recovery,
                            Ordinal: row.NormalAttempts, Deadline: row.Deadline));
                        await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                        if (candidate is not null)
                        {
                            receipt = ConfirmCandidate(candidate);
                            cleanup.RequireRecovery(receipt);
                        }
                    }
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(receipt, resources, probes.AsReadOnly(), candidate);
    }

    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> FinishCapacityRecoveryAsync(
        BackgroundJobCapacityRecoveryReceipt receipt, BackgroundJobFailureDecision decision, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(receipt);
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        BackgroundJobControlChange? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var row = await LockRowAsync(resources, receipt.JobId.Value, deadline.Token).ConfigureAwait(false);
                if (row is not null && MatchesRecovery(row, receipt) && row.Incomplete)
                {
                    var facts = await ReadCapacityFactsAsync(resources, row, receipt.AttemptId.Value, deadline.Token).ConfigureAwait(false);
                    if (facts is { Released: true, Positive: true })
                    {
                        var reason = decision.TerminalReason;
                        value = await RouteReleasedCapacityAsync(resources, row, reason, decision.ErrorCode ?? "unexpected_fault", deadline.Token).ConfigureAwait(false);
                        if (value is not null) probes.Add(Probe(row, value) with { CapacityGroup = row.CapacityGroup });
                        await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(resources.CommitAcknowledged ? value : null, resources, probes.AsReadOnly());
    }

    private static bool MatchesCleanup(ControlRow row, BackgroundJobActualCleanup cleanup) =>
        row.JobId == cleanup.Envelope.JobId.Value && row.TenantId == cleanup.Envelope.TenantId.Value
        && row.OrganizationId == cleanup.Envelope.OrganizationId?.Value && row.DefinitionKey == cleanup.Envelope.DefinitionKey
        && row.Profile == "transactional" && row.CapacityGroup is not null && row.CapacityGroup == cleanup.Envelope.CapacityGroup
        && row.Queue == cleanup.Envelope.Queue && row.OriginalDueAt == cleanup.Envelope.OriginalDueAt
        && row.WakeGeneration == cleanup.Claim.WakeGeneration
        && (cleanup.Receipt is null || row.AttemptId == cleanup.Receipt.AttemptId.Value
            && row.NormalAttempts == cleanup.Receipt.NormalAttemptOrdinal && row.Deadline == cleanup.Receipt.Deadline);

    private static bool MatchesRecovery(ControlRow row, BackgroundJobCapacityRecoveryReceipt receipt) =>
        row.JobId == receipt.JobId.Value && row.TenantId == receipt.TenantId.Value
        && row.OrganizationId == receipt.OrganizationId?.Value && row.DefinitionKey == receipt.DefinitionKey
        && row.Profile == receipt.Profile && row.Queue == receipt.Queue && row.CapacityGroup == receipt.CapacityGroup
        && row.OriginalDueAt == receipt.OriginalDueAt && row.WakeGeneration == receipt.WakeGeneration
        && row.AttemptId == receipt.AttemptId.Value && row.NormalAttempts == receipt.NormalAttemptOrdinal
        && row.Deadline == receipt.Deadline && row.LedgerToken == receipt.AdmissionToken
        && row.OwnershipToken == receipt.RecoveryToken && row.OwnershipKind == "recovery" && row.Phase == "reconciling";

    private async Task<bool> AcquireRecoveryTokenAsync(BackgroundJobControlResources resources, ControlRow row,
        Guid recovery, CancellationToken token)
    {
        var command = Command(resources, """
            UPDATE background_jobs SET phase='reconciling', ownership_token=@recovery,
                ownership_kind='recovery', ownership_until=clock_timestamp()+@lease, probe_after=NULL
            WHERE id=@id AND current_attempt_id=@attempt AND wake_generation=@generation
                AND ownership_token IS NOT DISTINCT FROM @token
                AND profile='transactional' AND capacity_group IS NOT NULL
                AND phase IN ('executing','reconciling')
                AND (ownership_token IS NULL OR ownership_until<=clock_timestamp())
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            """);
        AddRowFence(command, row);
        command.Parameters.AddWithValue("recovery", recovery);
        command.Parameters.AddWithValue("lease", options.LeaseDuration);
        var result = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        return result;
    }

    private async Task<Candidate?> ReadRecoveryCandidateAsync(BackgroundJobControlResources resources,
        Guid job, Guid recovery, CancellationToken token)
    {
        var command = Command(resources, RecoveryProjection);
        command.Parameters.AddWithValue("id", job);
        command.Parameters.AddWithValue("recovery", recovery);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        var result = await resources.Reader!.ReadAsync(token).ConfigureAwait(false) ? CaptureCandidate(resources.Reader) : null;
        await FinishAsync(resources).ConfigureAwait(false);
        return result;
    }

    private sealed record CapacityFacts(Guid AdmissionToken, int Ordinal, DateTimeOffset AdmittedAt,
        DateTimeOffset Deadline, DateTimeOffset? CleanupAt, DateTimeOffset? NoStartAt,
        bool Released, string? Reason)
    {
        internal bool Positive => Reason == "transactional_rollback" && CleanupAt is not null
            || Reason == "never_started" && NoStartAt is not null;
    }

    private async Task<CapacityFacts?> ReadCapacityFactsAsync(BackgroundJobControlResources resources,
        ControlRow row, Guid attempt, CancellationToken token)
    {
        var command = Command(resources, """
            SELECT a.admission_token, a.ordinal, a.admitted_at, a.deadline,
                a.transactional_cleanup_at, a.no_start_at, r.released_at IS NOT NULL, r.release_reason
            FROM background_job_attempts a JOIN background_job_reservations r
                ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
            WHERE a.id=@attempt AND a.tenant_id=@tenant AND a.job_id=@id AND a.stage='normal'
                AND a.organization_id IS NOT DISTINCT FROM @organization AND r.capacity_group=@group
            """);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("tenant", row.TenantId);
        command.Parameters.AddWithValue("id", row.JobId);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)row.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("group", row.CapacityGroup!);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        CapacityFacts? result = null;
        if (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
        {
            var r = resources.Reader;
            result = new(r.GetGuid(0), r.GetInt32(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3),
                TimeOrNull(r, 4), TimeOrNull(r, 5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7));
        }
        await FinishAsync(resources).ConfigureAwait(false);
        return result;
    }

    private async Task<BackgroundJobProbeResult> ProbeCapacityAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe probe, ControlRow? row, DateTimeOffset dbNow, CancellationToken token)
    {
        var disposition = BackgroundJobProbeDisposition.Unresolved;
        BackgroundJobCapacityRecoveryReceipt? receipt = null;
        var current = row is not null && row.Profile == "transactional" && row.CapacityGroup == probe.CapacityGroup
            && row.TenantId == probe.TenantId && row.OrganizationId == probe.OrganizationId
            && row.DefinitionKey == probe.DefinitionKey && row.Queue == probe.Queue;
        var authority = current && row!.WakeGeneration == probe.WakeGeneration && row.AttemptId == probe.AttemptId
            && row.NormalAttempts == probe.Ordinal && row.Deadline == probe.Deadline
            && row.MatchingAttempt && row.LedgerToken == probe.Token
            && row.OwnershipToken == probe.RecoveryToken && row.Phase == "reconciling" && row.OwnershipKind == "recovery"
            && row.OwnershipUntil > dbNow;
        if (current && probe.AttemptId is { } attempt)
        {
            // Read the descriptor's historical attempt, not merely the header's
            // current attempt. A takeover cannot erase an already committed fact.
            var facts = await ReadCapacityFactsAsync(resources, row!, attempt, token).ConfigureAwait(false);
            var exact = facts is not null && facts.AdmissionToken == probe.Token
                && facts.Ordinal == probe.Ordinal && facts.Deadline == probe.Deadline
                && (probe.AdmittedAt is null || facts.AdmittedAt == probe.AdmittedAt);
            if (exact && probe.Boundary == BackgroundJobCommitBoundary.TransactionalCleanup)
            {
                var expected = probe.CleanupEvidence == BackgroundJobCapacityCleanupEvidence.NeverStarted
                    ? facts!.NoStartAt is not null && facts.Reason == "never_started"
                    : facts!.CleanupAt is not null && facts.Reason == "transactional_rollback";
                if (expected && facts!.Released) disposition = BackgroundJobProbeDisposition.CapacityCleanupCommitted;
                else if (!facts!.Released && facts.CleanupAt is null && facts.NoStartAt is null)
                    disposition = BackgroundJobProbeDisposition.RolledBack;
            }
            else if (exact && probe.Boundary == BackgroundJobCommitBoundary.CapacityRecovery)
            {
                if (authority && row!.LedgerToken == probe.Token)
                {
                    var candidate = await ReadRecoveryCandidateAsync(resources, row.JobId, probe.RecoveryToken!.Value, token).ConfigureAwait(false);
                    if (candidate is not null) { receipt = ConfirmCandidate(candidate); disposition = BackgroundJobProbeDisposition.CapacityRecoveryAcquired; }
                }
                else disposition = BackgroundJobProbeDisposition.RolledBack;
            }
        }
        return new(disposition, probe.JobId, row?.Phase, row?.WakeGeneration ?? 0, row?.NormalAttempts ?? 0,
            row?.AttemptId, row?.OwnershipToken, row?.OwnershipUntil, row?.Deadline, dbNow)
        { RecoveryReceipt = receipt, CurrentRecoveryAuthority = authority };
    }

    private async Task<BackgroundJobControlChange?> RecoverCapacityRowAsync(BackgroundJobControlResources resources,
        ControlRow row, BackgroundJobTerminalReason? reason, string error, CancellationToken token)
    {
        if (row.Profile != "transactional" || row.CapacityGroup is null || !row.MatchingAttempt || row.AttemptId is not { } attempt) return null;
        var facts = await ReadCapacityFactsAsync(resources, row, attempt, token).ConfigureAwait(false);
        if (facts is { Released: true, Positive: true })
        {
            var recovery = ids.NewUuidV4();
            if (!await AcquireRecoveryTokenAsync(resources, row, recovery, token).ConfigureAwait(false)) return null;
            return await RouteReleasedCapacityAsync(resources, row with
            {
                Phase = "reconciling",
                OwnershipToken = recovery,
                OwnershipKind = "recovery"
            }, reason, error, token).ConfigureAwait(false);
        }
        // Missing local cleanup is recoverable parking, never irreversible
        // quarantine. Occupancy remains counted while the origin may return.
        var command = Command(resources, """
            UPDATE background_jobs SET phase='reconciling', ownership_token=NULL,
                ownership_kind=NULL, ownership_until=NULL, probe_after=clock_timestamp()+@delay,
                last_error='unknown_outcome'
            WHERE id=@id AND current_attempt_id=@attempt AND wake_generation=@generation
                AND ownership_token IS NOT DISTINCT FROM @token
                AND (ownership_token IS NULL OR ownership_until<=clock_timestamp())
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING id,wake_generation,phase,terminal_reason
            """);
        AddRowFence(command, row);
        command.Parameters.AddWithValue("delay", options.RecoveryProbeInterval);
        return await ReadChangeAsync(resources, token).ConfigureAwait(false);
    }

    private async Task<BackgroundJobControlChange?> RouteReleasedCapacityAsync(BackgroundJobControlResources resources,
        ControlRow row, BackgroundJobTerminalReason? reason, string error, CancellationToken token)
    {
        if (reason is not null || row.NormalAttempts == 6)
            return await RequestTerminalAsync(resources, row, reason ?? BackgroundJobTerminalReason.RetryExhausted,
                " AND phase='reconciling' AND ownership_kind='recovery' AND ownership_until>clock_timestamp()", token).ConfigureAwait(false);
        var command = Command(resources, """
            UPDATE background_jobs SET phase='pending', available_after=clock_timestamp()+@delay,
                last_error=@error, probe_after=NULL, ownership_token=NULL, ownership_kind=NULL, ownership_until=NULL
            WHERE id=@id AND current_attempt_id=@attempt AND ownership_token=@token AND wake_generation=@generation
                AND phase='reconciling' AND ownership_kind='recovery' AND ownership_until>clock_timestamp()
                AND normal_attempts BETWEEN 1 AND 5 AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING id,wake_generation,phase,terminal_reason
            """);
        AddRowFence(command, row);
        command.Parameters.AddWithValue("delay", BackgroundJobFailurePolicy.GetRetryDelay(row.NormalAttempts));
        command.Parameters.AddWithValue("error", error);
        return await ReadChangeAsync(resources, token).ConfigureAwait(false);
    }
}

using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    /// <summary>Schedule only positively ended normal history; never spend an admission or grant provider entry.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> RetryKnownExternalAsync(
        BackgroundJobExecutionIdentity identity, BackgroundJobExternalRetryMode mode, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(identity);
        var origin = BackgroundJobCommitProbe.ForExternalKnownRetry(identity);
        if (!Enum.IsDefined(mode) || !ValidKnownRetryOrigin(origin)
            || identity.IsRecovery != (mode == BackgroundJobExternalRetryMode.RecoveryKnown))
            throw new ArgumentException("Known retry requires its exact normal external owner and finite mode.", nameof(identity));
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        BackgroundJobControlChange? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false)
                && await LockExternalJobAsync(resources, origin.JobId, deadline.Token).ConfigureAwait(false))
            {
                var row = await LockRowAsync(resources, origin.JobId, deadline.Token).ConfigureAwait(false);
                if (row is not null && MatchesKnownRetryRow(row, origin) && MatchesKnownRetryOwner(row, origin)
                    && definitions.Resolve(row.DefinitionKey) is
                    {
                        Disposition: BackgroundJobDefinitionDisposition.Active,
                        Definition: { Profile: BackgroundJobExecutionProfile.External } definition
                    }
                    && definition.Queue == row.Queue && definition.CapacityGroup == row.CapacityGroup
                    && (definition.Scope == BackgroundJobScope.TenantWide) == (row.OrganizationId is null)
                    && await ReadKnownRetryHistoryAsync(resources, origin, deadline.Token).ConfigureAwait(false) is { } history
                    && (mode == BackgroundJobExternalRetryMode.RecoveryKnown || history[^1].OutcomeKind is not null))
                {
                    var token = identity.Token;
                    if (mode == BackgroundJobExternalRetryMode.OriginalCompleted)
                        token = await HandoffExternalNormalOwnerAsync(resources, origin, deadline.Token).ConfigureAwait(false);
                    if (token != Guid.Empty)
                    {
                        var command = Command(resources, """
                            UPDATE background_jobs j SET phase='pending',available_after=clock_timestamp()+@delay,
                                probe_after=NULL,ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL
                            WHERE
                            """ + " " + ExternalNormalOwnerFence + " " + """
                                AND j.phase='reconciling' AND j.ownership_kind='recovery'
                                AND j.ownership_until>clock_timestamp()
                            RETURNING j.id,j.wake_generation,j.phase,j.terminal_reason,j.available_after
                            """);
                        AddExternalNormalOwnerFence(command, origin, token);
                        command.Parameters.AddWithValue("delay", BackgroundJobFailurePolicy.GetRetryDelay(identity.NormalAttemptOrdinal));
                        resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                        if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                        {
                            var returned = resources.Reader;
                            value = new(returned.GetGuid(0), returned.GetInt64(1), returned.GetString(2),
                                returned.IsDBNull(3) ? null : returned.GetString(3));
                            if (value.JobId != origin.JobId || value.WakeGeneration != checked(origin.WakeGeneration + 1)
                                || value.Phase != "pending" || value.TerminalReason is not null)
                                throw new InvalidOperationException("Known retry returned a contradictory eligibility cycle.");
                            probes.Add(origin with
                            {
                                ExpectedGeneration = value.WakeGeneration,
                                ExpectedPhase = value.Phase,
                                ExpectedAvailableAfter = returned.GetFieldValue<DateTimeOffset>(4)
                            });
                        }
                        await FinishAsync(resources).ConfigureAwait(false);
                        // A refused final UPDATE rolls back the private shortening/handoff too.
                        if (value is not null) await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(resources.CommitAcknowledged ? value : null, resources, probes.AsReadOnly());
    }

    private const string ExternalNormalOwnerFence = """
        j.id=@id AND j.tenant_id=@tenant AND j.organization_id IS NOT DISTINCT FROM @organization
        AND j.definition_key=@definition AND j.queue=@queue AND j.capacity_group=@capacity
        AND j.original_due_at=@due AND j.profile='external' AND j.current_attempt_id=@attempt
        AND j.normal_attempts=@ordinal AND j.normal_attempts BETWEEN 1 AND 6 AND j.finalize_attempts=0
        AND j.final_outcome_attempt_id IS NULL AND j.finalization_status IS NULL AND j.finalization_reason IS NULL
        AND NOT j.business_finalization_incomplete AND j.attempt_deadline=@deadline
        AND j.wake_generation=@generation AND j.ownership_token=@token
        AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL AND j.terminal_reason IS NULL
        AND EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.id=@attempt AND a.job_id=j.id
            AND a.tenant_id=j.tenant_id AND a.organization_id IS NOT DISTINCT FROM j.organization_id
            AND a.stage='normal' AND a.ordinal=@ordinal AND a.admission_token=@admission
            AND a.admitted_at=@admitted AND a.deadline=@deadline AND a.final_outcome_attempt_id IS NULL)
        """;

    private async Task<Guid> HandoffExternalNormalOwnerAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, CancellationToken cancellationToken)
    {
        var command = Command(resources, """
            WITH retry_clock AS MATERIALIZED (SELECT clock_timestamp() AS at)
            UPDATE background_jobs j SET ownership_until=retry_clock.at FROM retry_clock WHERE
            """ + " " + ExternalNormalOwnerFence + " " + """
                AND j.phase='executing' AND j.ownership_kind='execution'
                AND j.ownership_until>retry_clock.at AND j.attempt_deadline>retry_clock.at
            """);
        AddExternalNormalOwnerFence(command, origin, origin.Token);
        var shortened = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        if (!shortened) return Guid.Empty;
        var recovery = ids.NewUuidV4();
        if (recovery == Guid.Empty || recovery == origin.Token)
            throw new InvalidOperationException("Private retry handoff requires a fresh token.");
        command = Command(resources, """
            UPDATE background_jobs j SET phase='reconciling',ownership_kind='recovery',ownership_token=@recovery,
                ownership_until=clock_timestamp()+@lease,probe_after=NULL WHERE
            """ + " " + ExternalNormalOwnerFence + " " + """
                AND j.phase='executing' AND j.ownership_kind='execution' AND j.ownership_until<=clock_timestamp()
            """);
        AddExternalNormalOwnerFence(command, origin, origin.Token);
        command.Parameters.AddWithValue("recovery", recovery);
        command.Parameters.AddWithValue("lease", options.LeaseDuration);
        var acquired = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        return acquired ? recovery : Guid.Empty;
    }

    private static void AddExternalNormalOwnerFence(NpgsqlCommand command, BackgroundJobCommitProbe origin, Guid token)
    {
        command.Parameters.AddWithValue("id", origin.JobId);
        command.Parameters.AddWithValue("tenant", origin.TenantId);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)origin.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("definition", origin.DefinitionKey);
        command.Parameters.AddWithValue("queue", origin.Queue);
        command.Parameters.AddWithValue("capacity", origin.CapacityGroup!);
        command.Parameters.AddWithValue("due", origin.OriginalDueAt!.Value);
        command.Parameters.AddWithValue("attempt", origin.AttemptId!.Value);
        command.Parameters.AddWithValue("ordinal", origin.Ordinal!.Value);
        command.Parameters.AddWithValue("deadline", origin.Deadline!.Value);
        command.Parameters.AddWithValue("generation", origin.WakeGeneration);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("admission", origin.OriginalAdmissionToken!.Value);
        command.Parameters.AddWithValue("admitted", origin.AdmittedAt!.Value);
    }

    private static bool ValidKnownRetryOrigin(BackgroundJobCommitProbe origin) =>
        origin.Boundary == BackgroundJobCommitBoundary.ExternalKnownRetry && origin.Profile == "external"
        && origin.LedgerStage == BackgroundJobLedgerStage.Normal && origin.Ordinal is >= 1 and <= 5
        && origin.FinalizeOrdinal == 0 && origin.SelectedNormalAttemptId is null
        && origin.CapacityGroup is not null && origin.AttemptId is not null
        && origin.AdmittedAt is not null && origin.Deadline is not null && origin.OriginalDueAt is not null
        && origin.OriginalAdmissionToken is { } admission && admission != Guid.Empty && origin.Token != Guid.Empty
        && (origin.IsRecovery ? origin.Token != admission : origin.Token == admission);

    private static bool MatchesKnownRetryRow(ControlRow row, BackgroundJobCommitProbe origin) =>
        row.JobId == origin.JobId && row.TenantId == origin.TenantId && row.OrganizationId == origin.OrganizationId
        && row.DefinitionKey == origin.DefinitionKey && row.Queue == origin.Queue && row.CapacityGroup == origin.CapacityGroup
        && row.Profile == "external" && row.OriginalDueAt == origin.OriginalDueAt
        && row.AttemptId == origin.AttemptId && row.NormalAttempts == origin.Ordinal && row.FinalizeAttempts == 0
        && row.SelectedNormalAttemptId is null && row.Deadline == origin.Deadline && row.MatchingNormalAttempt
        && row.LedgerToken == origin.OriginalAdmissionToken && row.LedgerAdmittedAt == origin.AdmittedAt && row.Incomplete;

    private static bool MatchesKnownRetryOwner(ControlRow row, BackgroundJobCommitProbe origin) =>
        row.WakeGeneration == origin.WakeGeneration && row.OwnershipToken == origin.Token
        && row.Phase == (origin.IsRecovery ? "reconciling" : "executing")
        && row.OwnershipKind == (origin.IsRecovery ? "recovery" : "execution");

    private async Task<IReadOnlyList<BackgroundJobCheckpointHistoryFacts>?> ReadKnownRetryHistoryAsync(
        BackgroundJobControlResources resources, BackgroundJobCommitProbe origin, CancellationToken token)
    {
        var command = Command(resources, CheckpointHistoryColumns + " WHERE j.id=@id AND a.stage='normal' ORDER BY a.ordinal LIMIT 7");
        command.Parameters.AddWithValue("id", origin.JobId);
        var rows = new List<BackgroundJobCheckpointHistoryFacts>();
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        while (await resources.Reader!.ReadAsync(token).ConfigureAwait(false)) rows.Add(CaptureSelectedNormalHistory(resources.Reader));
        await FinishAsync(resources).ConfigureAwait(false);
        if (rows.Count != origin.Ordinal) return null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!ValidHistoryIdentity(row) || !ValidNormalHistory(row) || row.Ordinal != i + 1
                || row.JobId != origin.JobId || row.TenantId != origin.TenantId || row.OrganizationId != origin.OrganizationId
                || row.DefinitionKey != origin.DefinitionKey || row.Queue != origin.Queue || row.CapacityGroup != origin.CapacityGroup
                || row.OriginalDueAt != origin.OriginalDueAt || row.CurrentAttemptId != origin.AttemptId
                || row.NormalAttempts != origin.Ordinal || row.FinalizeAttempts != 0
                || row.JobSelectedNormalAttemptId is not null || row.FinalizationStatus is not null || row.FinalizationReason is not null
                || row.BusinessFinalizationIncomplete || row.ProcessedAt is not null || row.DeadLetteredAt is not null || row.TerminalReason is not null
                || !PositiveRetryHistory(row))
                return null;
        }
        var current = rows[^1];
        return current.AttemptId == origin.AttemptId && current.AdmissionToken == origin.OriginalAdmissionToken
            && current.AdmittedAt == origin.AdmittedAt && current.Deadline == origin.Deadline
            && current.CurrentAttemptDeadline == origin.Deadline ? rows.AsReadOnly() : null;
    }

    private static bool PositiveRetryHistory(BackgroundJobCheckpointHistoryFacts row) => row.NoStartAt is not null
        || row.OutcomeKind is "failed" or "cancelled" && row.FailurePolicy == "transient_repeatable";

    private async Task<BackgroundJobProbeDisposition> ProbeKnownExternalRetryAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, ControlRow? row, CancellationToken token)
    {
        // No generic completed-state or changed-token fallback can certify this transaction.
        if (!ValidKnownRetryOrigin(origin) || origin.ExpectedGeneration != origin.WakeGeneration + 1
            || origin.ExpectedPhase != "pending" || origin.ExpectedAvailableAfter is null)
            return BackgroundJobProbeDisposition.Unresolved;
        if (row is null) return BackgroundJobProbeDisposition.Unresolved;
        if (!MatchesKnownRetryRow(row, origin)) return BackgroundJobProbeDisposition.ChangedOwner;
        if (await ReadKnownRetryHistoryAsync(resources, origin, token).ConfigureAwait(false) is not { } history
            || !origin.IsRecovery && history[^1].OutcomeKind is null) return BackgroundJobProbeDisposition.Unresolved;
        if (MatchesKnownRetryOwner(row, origin)) return BackgroundJobProbeDisposition.RolledBack;
        if (row.WakeGeneration != origin.ExpectedGeneration || row.Phase != "pending"
            || !(row.OwnershipToken is null && row.OwnershipKind is null && row.OwnershipUntil is null
                || row.OwnershipToken is { } owner && owner != Guid.Empty && row.OwnershipKind == "routing" && row.OwnershipUntil is not null))
            return BackgroundJobProbeDisposition.ChangedOwner;
        var command = Command(resources, "SELECT available_after FROM background_jobs WHERE id=@id");
        command.Parameters.AddWithValue("id", origin.JobId);
        var available = (DateTime)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        return new DateTimeOffset(available) == origin.ExpectedAvailableAfter
            ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.Unresolved;
    }
}

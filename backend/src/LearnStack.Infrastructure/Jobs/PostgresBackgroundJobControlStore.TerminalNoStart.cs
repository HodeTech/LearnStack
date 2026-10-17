using LearnStack.Application.Jobs;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    // Keep the reservation probe correlated to each due job. Pulling it into
    // a semi-join can scan unrelated occupied work even for a single due job.
    internal const string TerminalNoStartSelectionSql = "SELECT j.id FROM background_jobs j WHERE ("
        + BackgroundJobTerminalEligibility.TerminalNoStartBefore + ")" + """
            AND (j.ownership_token IS NULL OR j.ownership_until<=statement_timestamp())
            AND j.available_after<=statement_timestamp() AND NOT (j.id=ANY(@blocked))
            AND EXISTS (SELECT 1 FROM background_job_reservations r
                WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id AND r.attempt_id=j.current_attempt_id
                    AND r.capacity_group=j.capacity_group AND r.released_at IS NULL OFFSET 0)
        ORDER BY j.available_after,j.created_at,j.id LIMIT 1 FOR UPDATE SKIP LOCKED
        """;

    /// <summary>The existing terminal lane acquires cleanup only; no terminal audit or provider entry is granted.</summary>
    internal async Task<OwnedBackgroundJobExternalRecovery> AcquireExternalTerminalNoStartAsync(
        IReadOnlyCollection<Guid> retainedJobIds, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(retainedJobIds);
        if (runtimeGate is null || retainedJobIds.Count > BackgroundJobControlLimits.MaximumRetainedIds)
            throw new ArgumentException("Terminal no-start requires its process and bounded retained inventory.", nameof(retainedJobIds));
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        ExternalRecoveryCandidate? candidate = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var command = Command(resources, TerminalNoStartSelectionSql);
                command.Parameters.AddWithValue("blocked", retainedJobIds.ToArray());
                var selected = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false) as Guid?;
                await resources.FinishCommandAsync().ConfigureAwait(false);
                var row = selected is { } id ? await LockRowAsync(resources, id, deadline.Token).ConfigureAwait(false) : null;
                if (row is
                    {
                        Profile: "external", Phase: "terminal_pending", ProcessedAt: null, DeadLetteredAt: null,
                        CapacityGroup: not null, LedgerAdmittedAt: not null, FinalizeAttempts: 0, SelectedNormalAttemptId: null
                    }
                    && row.MatchingNormalAttempt && row.NormalAttempts is >= 1 and <= 6
                    && TerminalNoStartReason(row.TerminalReason, row.NormalAttempts)
                    && await ReadTerminalNoStartHistoryAsync(resources, NoPlanHistoryOrigin(row), row.TerminalReason,
                        deadline.Token).ConfigureAwait(false))
                {
                    var recovery = ids.NewUuidV4();
                    if (recovery == Guid.Empty || recovery == row.LedgerToken || recovery == row.OwnershipToken)
                        throw new InvalidOperationException("Terminal no-start requires a fresh distinct recovery token.");
                    command = Command(resources, """
                        UPDATE background_jobs j SET ownership_token=@recovery,ownership_kind='recovery',
                            ownership_until=clock_timestamp()+@lease,probe_after=NULL
                        WHERE id=@id AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization
                            AND definition_key=@definition AND queue=@queue AND capacity_group=@capacity
                            AND profile='external' AND phase='terminal_pending' AND terminal_reason=@reason
                            AND wake_generation=@generation AND current_attempt_id=@attempt
                            AND normal_attempts=@ordinal AND finalize_attempts=0 AND finalization_status IS NULL
                            AND final_outcome_attempt_id IS NULL
                            AND ownership_token IS NOT DISTINCT FROM @token
                            AND (ownership_token IS NULL OR ownership_until<=clock_timestamp())
                            AND processed_at IS NULL AND dead_lettered_at IS NULL
                        """ + " AND (" + BackgroundJobTerminalEligibility.TerminalNoStartBefore + ")");
                    AddRowFence(command, row);
                    command.Parameters.AddWithValue("tenant", row.TenantId);
                    command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)row.OrganizationId ?? DBNull.Value);
                    command.Parameters.AddWithValue("definition", row.DefinitionKey);
                    command.Parameters.AddWithValue("queue", row.Queue);
                    command.Parameters.AddWithValue("capacity", row.CapacityGroup);
                    command.Parameters.AddWithValue("reason", row.TerminalReason!);
                    command.Parameters.AddWithValue("ordinal", row.NormalAttempts);
                    command.Parameters.AddWithValue("recovery", recovery);
                    command.Parameters.AddWithValue("lease", options.LeaseDuration);
                    var acquired = await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) == 1;
                    await resources.FinishCommandAsync().ConfigureAwait(false);
                    if (acquired)
                    {
                        candidate = await ReadExternalRecoveryAsync(resources, row.JobId, row.WakeGeneration, recovery,
                            deadline.Token).ConfigureAwait(false);
                        if (candidate is null) throw new InvalidOperationException("Terminal no-start acquisition returned no exact receipt.");
                        probes.Add(new(BackgroundJobCommitBoundary.ExternalRecovery, row.JobId, row.TenantId,
                            row.OrganizationId, row.DefinitionKey, row.Queue, row.WakeGeneration, row.AttemptId, row.LedgerToken!.Value,
                            ExpectedUntil: row.OwnershipUntil,
                            CapacityGroup: row.CapacityGroup, RecoveryToken: recovery, Ordinal: row.NormalAttempts,
                            Deadline: row.Deadline, AdmittedAt: row.LedgerAdmittedAt, OriginalDueAt: row.OriginalDueAt,
                            ExpectedPhase: row.Phase, ExpectedTerminalReason: row.TerminalReason));
                        await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        var receipt = candidate is not null && resources.CommitAcknowledged && resources.CleanupConfirmed && resources.Failure is null
            ? PublishExternalRecoveryCandidate(candidate) : null;
        return new(new(receipt, resources, probes.AsReadOnly(), candidate), runtimeGate.RunStop);
    }

    private static BackgroundJobCommitProbe NoPlanHistoryOrigin(ControlRow row) => new(
        BackgroundJobCommitBoundary.ExternalNoPlanTerminalRequest, row.JobId, row.TenantId, row.OrganizationId,
        row.DefinitionKey, row.Queue, row.WakeGeneration, row.AttemptId, row.OwnershipToken ?? Guid.Empty,
        CapacityGroup: row.CapacityGroup, Ordinal: row.NormalAttempts, Deadline: row.Deadline, AdmittedAt: row.LedgerAdmittedAt,
        OriginalDueAt: row.OriginalDueAt, OriginalAdmissionToken: row.LedgerToken);
}

using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    /// <summary>Persist a no-plan terminal decision before any app NoStart or occupancy release.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> RequestNoPlanExternalTerminalAsync(
        BackgroundJobExecutionIdentity identity, BackgroundJobTerminalReason reason, CancellationToken cancellationToken,
        JobBusinessExecutionResult? cleanedPrepare = null)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(identity);
        var origin = NoPlanTerminalRequestOrigin(identity, reason);
        if (cleanedPrepare is not null) RequireCleanedPermanentPrepare(identity, reason, cleanedPrepare);
        if (runtimeGate is null || !ValidNoPlanTerminalOrigin(origin)
            || !identity.IsRecovery && identity.Stage != BackgroundJobExecutionStage.Prepare)
            throw new ArgumentException("No-plan terminal status requires an exact finite normal owner and reason.", nameof(identity));
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        BackgroundJobControlChange? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false)
                && (cleanedPrepare is not null
                    || await LockExternalJobAsync(resources, origin.JobId, deadline.Token).ConfigureAwait(false)))
            {
                // A cleaned Permanent result must wait within the SQL budget;
                // SKIP LOCKED cannot silently consume its original policy.
                var row = await LockRowAsync(resources, origin.JobId, deadline.Token).ConfigureAwait(false);
                var unchangedOwner = row is not null && row.Incomplete && MatchesNoPlanTerminalIdentity(row, origin)
                    && MatchesNoPlanTerminalOwner(row, origin);
                if (row is not null && unchangedOwner
                    && definitions.Resolve(row.DefinitionKey) is
                    {
                        Disposition: BackgroundJobDefinitionDisposition.Active,
                        Definition: { Profile: BackgroundJobExecutionProfile.External } definition
                    }
                    && definition.Queue == row.Queue && definition.CapacityGroup == row.CapacityGroup
                    && (definition.Scope == BackgroundJobScope.TenantWide) == (row.OrganizationId is null)
                    && await ReadNoPlanTerminalHistoryAsync(resources, origin, null, deadline.Token).ConfigureAwait(false))
                {
                    var command = Command(resources, """
                        UPDATE background_jobs j SET phase='terminal_pending',terminal_reason=@reason,
                            available_after=clock_timestamp(),probe_after=NULL,
                            ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL
                        WHERE
                        """ + " " + ExternalNormalOwnerFence + " " + """
                            AND j.phase=@phase AND j.ownership_kind=@kind
                            AND (@cleanedPrepare OR (j.ownership_until>clock_timestamp()
                                AND (@recovered OR j.attempt_deadline>clock_timestamp())))
                            AND j.plan_fingerprint IS NULL AND j.prepared_at IS NULL AND j.operation_id IS NULL
                        RETURNING j.id,j.wake_generation,j.phase,j.terminal_reason
                        """);
                    AddExternalNormalOwnerFence(command, origin, origin.Token);
                    command.Parameters.AddWithValue("reason", origin.ExpectedTerminalReason!);
                    command.Parameters.AddWithValue("phase", origin.IsRecovery ? "reconciling" : "preparing");
                    command.Parameters.AddWithValue("kind", origin.IsRecovery ? "recovery" : "execution");
                    command.Parameters.AddWithValue("recovered", origin.IsRecovery);
                    command.Parameters.AddWithValue("cleanedPrepare", cleanedPrepare is not null);
                    resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                    if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                    {
                        var returned = resources.Reader;
                        value = new(returned.GetGuid(0), returned.GetInt64(1), returned.GetString(2), returned.GetString(3));
                        if (value.JobId != origin.JobId || value.WakeGeneration != origin.WakeGeneration
                            || value.Phase != origin.ExpectedPhase || value.TerminalReason != origin.ExpectedTerminalReason)
                            throw new InvalidOperationException("No-plan terminal request returned contradictory status.");
                        probes.Add(origin);
                    }
                    await FinishAsync(resources).ConfigureAwait(false);
                    if (cleanedPrepare is not null && value is null)
                        throw new InvalidOperationException("The unchanged cleaned Prepare owner lost its exact status fence.");
                    deadline.Token.ThrowIfCancellationRequested();
                    if (value is not null && runtimeGate.TryAdmit()) await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                }
                else if (cleanedPrepare is not null && unchangedOwner)
                    throw new InvalidOperationException("The retained Prepare result requires exact positive NoPlan history.");
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(resources.CommitAcknowledged ? value : null, resources, probes.AsReadOnly(), cleanedPrepare);
    }

    internal static BackgroundJobCommitProbe NoPlanTerminalRequestOrigin(BackgroundJobExecutionIdentity identity,
        BackgroundJobTerminalReason reason) => BackgroundJobCommitProbe.ForRenewal(identity, null) with
        {
            Boundary = BackgroundJobCommitBoundary.ExternalNoPlanTerminalRequest,
            ExpectedPhase = "terminal_pending",
            ExpectedGeneration = identity.WakeGeneration,
            ExpectedTerminalReason = BackgroundJobFailurePolicy.TerminalCode(reason),
        };

    private static void RequireCleanedPermanentPrepare(BackgroundJobExecutionIdentity identity,
        BackgroundJobTerminalReason reason, JobBusinessExecutionResult execution)
    {
        var receipt = execution.Receipt;
        var envelope = receipt.Envelope;
        if (identity.IsRecovery || identity.Profile != "external" || identity.Stage != BackgroundJobExecutionStage.Prepare
            || identity.LedgerStage != BackgroundJobLedgerStage.Normal || identity.FinalizeAttemptOrdinal != 0
            || identity.SelectedNormalAttemptId is not null || reason != BackgroundJobTerminalReason.PermanentFailure
            || receipt.Stage != BackgroundJobExecutionStage.Prepare || receipt.LedgerStage != BackgroundJobLedgerStage.Normal
            || receipt.FinalizeAttemptOrdinal != 0 || receipt.SelectedNormalAttemptId is not null
            || receipt.AttemptId != identity.AttemptId || receipt.Claim.Token != identity.Token
            || receipt.Claim.Token != identity.AdmissionToken || receipt.Claim.WakeGeneration != identity.WakeGeneration
            || receipt.NormalAttemptOrdinal != identity.NormalAttemptOrdinal || receipt.AdmittedAt != identity.AdmittedAt
            || receipt.Deadline != identity.Deadline || receipt.OwnershipUntil != identity.OwnershipUntil
            || envelope.JobId != identity.JobId || envelope.TenantId != identity.TenantId
            || envelope.OrganizationId != identity.OrganizationId || envelope.DefinitionKey != identity.DefinitionKey
            || envelope.Queue != identity.Queue || envelope.CapacityGroup != identity.CapacityGroup
            || envelope.Profile != identity.Profile || envelope.OriginalDueAt != identity.OriginalDueAt
            || !ReferenceEquals(receipt.Run, execution.Gate.Run) || execution.Gate.Kind != BackgroundJobCheckpointKind.Prepare
            || !execution.ExecutionIssued || execution.CommitAcknowledged || execution.DurableOutcomeUncertain
            || !execution.KnownRollback || execution.CleanupFailed || !execution.SourceOwner.CleanupConfirmed
            || execution.Decision.Disposition != BackgroundJobFailureDisposition.Terminal
            || execution.Decision.TerminalReason != BackgroundJobTerminalReason.PermanentFailure
            || execution.Decision.ErrorCode != "permanent_failure")
            throw new ArgumentException("Expired-owner accounting requires the exact positively cleaned Permanent Prepare result.", nameof(execution));
    }

    private static bool ValidNoPlanTerminalOrigin(BackgroundJobCommitProbe origin) =>
        origin.Boundary == BackgroundJobCommitBoundary.ExternalNoPlanTerminalRequest
        && origin.Profile == "external" && origin.LedgerStage == BackgroundJobLedgerStage.Normal
        && origin.Ordinal is >= 1 and <= 6 && origin.FinalizeOrdinal == 0 && origin.SelectedNormalAttemptId is null
        && origin.CapacityGroup is not null && origin.AttemptId is not null
        && origin.AdmittedAt is not null && origin.Deadline is not null && origin.OriginalDueAt is not null
        && origin.OriginalAdmissionToken is { } admission && admission != Guid.Empty && origin.Token != Guid.Empty
        && (origin.IsRecovery ? origin.Token != admission : origin.Token == admission)
        && origin.ExpectedGeneration == origin.WakeGeneration && origin.ExpectedPhase == "terminal_pending"
        && (origin.ExpectedTerminalReason == "permanent_failure" && !origin.IsRecovery
            || origin.ExpectedTerminalReason == "retry_exhausted" && origin.Ordinal == 6);

    // This identity deliberately excludes mutable completion/reason/owner status:
    // an exact historical request can survive later app cleanup or terminal audit.
    private static bool MatchesNoPlanTerminalIdentity(ControlRow row, BackgroundJobCommitProbe origin) =>
        row.JobId == origin.JobId && row.TenantId == origin.TenantId && row.OrganizationId == origin.OrganizationId
        && row.DefinitionKey == origin.DefinitionKey && row.Queue == origin.Queue && row.CapacityGroup == origin.CapacityGroup
        && row.Profile == "external" && row.OriginalDueAt == origin.OriginalDueAt
        && row.AttemptId == origin.AttemptId && row.NormalAttempts == origin.Ordinal && row.FinalizeAttempts == 0
        && row.SelectedNormalAttemptId is null && row.Deadline == origin.Deadline && row.MatchingNormalAttempt
        && row.LedgerToken == origin.OriginalAdmissionToken && row.LedgerAdmittedAt == origin.AdmittedAt
        && row.WakeGeneration == origin.WakeGeneration;

    private static bool MatchesNoPlanTerminalOwner(ControlRow row, BackgroundJobCommitProbe origin) =>
        row.OwnershipToken == origin.Token && row.Phase == (origin.IsRecovery ? "reconciling" : "preparing")
        && row.OwnershipKind == (origin.IsRecovery ? "recovery" : "execution");

    private async Task<bool> ReadNoPlanTerminalHistoryAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, string? expectedReason, CancellationToken token)
    {
        var command = Command(resources, CheckpointHistoryColumns + " WHERE j.id=@id AND a.stage='normal' ORDER BY a.ordinal LIMIT 7");
        command.Parameters.AddWithValue("id", origin.JobId);
        var rows = new List<BackgroundJobCheckpointHistoryFacts>();
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        while (await resources.Reader!.ReadAsync(token).ConfigureAwait(false)) rows.Add(CaptureSelectedNormalHistory(resources.Reader));
        await FinishAsync(resources).ConfigureAwait(false);
        if (rows.Count != origin.Ordinal) return false;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!ValidHistoryIdentity(row) || !ValidNormalHistory(row) || row.Ordinal != i + 1
                || row.JobId != origin.JobId || row.TenantId != origin.TenantId || row.OrganizationId != origin.OrganizationId
                || row.DefinitionKey != origin.DefinitionKey || row.Queue != origin.Queue || row.CapacityGroup != origin.CapacityGroup
                || row.OriginalDueAt != origin.OriginalDueAt || row.CurrentAttemptId != origin.AttemptId
                || row.CurrentWakeGeneration != origin.WakeGeneration || row.NormalAttempts != origin.Ordinal || row.FinalizeAttempts != 0
                || row.JobSelectedNormalAttemptId is not null || row.FinalizationStatus is not null || row.FinalizationReason is not null
                || row.BusinessFinalizationIncomplete || row.ProcessedAt is not null || row.TerminalReason != expectedReason
                || expectedReason is null && row.DeadLetteredAt is not null
                || row.PlanFingerprint is not null || row.OperationId is not null || row.PreparedAt is not null
                || row.AttemptPlanFingerprint is not null || row.AttemptOperationId is not null
                || row.ExecuteIntentAt is not null || row.OutcomeKind is not null
                || i < rows.Count - 1 && row.NoStartAt is null)
                return false;
        }
        var current = rows[^1];
        return current.AttemptId == origin.AttemptId && current.AdmissionToken == origin.OriginalAdmissionToken
            && current.AdmittedAt == origin.AdmittedAt && current.Deadline == origin.Deadline
            && current.CurrentAttemptDeadline == origin.Deadline
            && (current.DeadLetteredAt is null || current.NoStartAt is not null);
    }

    private async Task<BackgroundJobProbeDisposition> ProbeNoPlanTerminalRequestAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, ControlRow? row, CancellationToken token)
    {
        if (!ValidNoPlanTerminalOrigin(origin)) return BackgroundJobProbeDisposition.Unresolved;
        if (row is null) return BackgroundJobProbeDisposition.Unresolved;
        if (!MatchesNoPlanTerminalIdentity(row, origin)) return BackgroundJobProbeDisposition.ChangedOwner;
        if (row.Phase == "terminal_pending")
            return row.TerminalReason == origin.ExpectedTerminalReason && ValidLaterNoPlanOwner(row, origin)
                && await ReadNoPlanTerminalHistoryAsync(resources, origin, origin.ExpectedTerminalReason, token).ConfigureAwait(false)
                ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.Unresolved;
        if (row.Incomplete && MatchesNoPlanTerminalOwner(row, origin))
            return await ReadNoPlanTerminalHistoryAsync(resources, origin, null, token).ConfigureAwait(false)
                ? BackgroundJobProbeDisposition.RolledBack : BackgroundJobProbeDisposition.Unresolved;
        return BackgroundJobProbeDisposition.ChangedOwner;
    }

    private static bool ValidLaterNoPlanOwner(ControlRow row, BackgroundJobCommitProbe origin) =>
        row.OwnershipToken is null && row.OwnershipKind is null && row.OwnershipUntil is null
        || row.DeadLetteredAt is null && row.OwnershipToken is { } token && token != Guid.Empty
            && token != origin.Token && token != origin.OriginalAdmissionToken
            && row.OwnershipKind == "recovery" && row.OwnershipUntil is not null;
}

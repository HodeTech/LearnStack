using LearnStack.Application.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    private static bool TerminalNoStartReason(string? reason, int? ordinal) => ordinal is >= 1 and <= 6
        && (reason is "permanent_failure" or "retired_definition" or "invalid_envelope"
            || reason == "retry_exhausted" && ordinal == 6);

    private async Task<bool> ReadTerminalNoStartHistoryAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, string? expectedReason, CancellationToken token)
    {
        if (!TerminalNoStartReason(expectedReason, origin.Ordinal) || origin.AttemptId is not { } attempt) return false;
        var command = Command(resources, CheckpointHistoryProjection);
        command.Parameters.AddWithValue("id", origin.JobId);
        command.Parameters.AddWithValue("attempt", attempt);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        var current = await resources.Reader!.ReadAsync(token).ConfigureAwait(false)
            ? CaptureSelectedNormalHistory(resources.Reader) : null;
        await FinishAsync(resources).ConfigureAwait(false);
        if (current is null || !ValidHistoryIdentity(current) || !ValidNormalHistory(current)
            || current.JobId != origin.JobId || current.TenantId != origin.TenantId || current.OrganizationId != origin.OrganizationId
            || current.DefinitionKey != origin.DefinitionKey || current.Queue != origin.Queue || current.CapacityGroup != origin.CapacityGroup
            || current.OriginalDueAt != origin.OriginalDueAt || current.AttemptId != origin.AttemptId
            || current.AdmissionToken != (origin.OriginalAdmissionToken ?? origin.Token)
            || current.AdmittedAt != origin.AdmittedAt || current.Deadline != origin.Deadline
            || current.CurrentAttemptId != origin.AttemptId || current.CurrentAttemptDeadline != origin.Deadline
            || current.CurrentWakeGeneration != origin.WakeGeneration || current.NormalAttempts != origin.Ordinal
            || current.FinalizeAttempts != 0 || current.JobSelectedNormalAttemptId is not null
            || current.FinalizationStatus is not null || current.FinalizationReason is not null
            || current.Phase != "terminal_pending" || current.TerminalReason != expectedReason
            || current.ExecuteIntentAt is not null || current.OutcomeKind is not null)
            return false;
        return await ReadTerminalNoStartProofAsync(resources, current.JobId, token).ConfigureAwait(false);
    }

    private async Task<bool> ReadTerminalNoStartProofAsync(BackgroundJobControlResources resources, Guid job,
        CancellationToken token)
    {
        var command = Command(resources, "SELECT TRUE FROM background_jobs j WHERE j.id=@id AND ("
            + BackgroundJobTerminalEligibility.TerminalNoStartHistory + ")");
        command.Parameters.AddWithValue("id", job);
        var valid = await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        return valid;
    }

    private async Task<BackgroundJobCheckpointHistoryDisposition> ClassifyTerminalNoStartHistoryAsync(
        BackgroundJobControlResources resources, BackgroundJobCheckpointHistoryOrigin origin,
        BackgroundJobCheckpointHistoryFacts row, CancellationToken token)
    {
        var terminal = origin.Checkpoint!.Outcome!;
        if (!terminal.IsTerminalNoStart || !ValidNormalHistory(row)
            || row.Phase != "terminal_pending" || row.TerminalReason != terminal.Authority.TerminalReason
            || row.CurrentWakeGeneration != terminal.Authority.WakeGeneration
            || row.CurrentAttemptId != terminal.Authority.AttemptId.Value || row.NormalAttempts != terminal.Authority.NormalAttemptOrdinal
            || row.FinalizeAttempts != 0 || row.JobSelectedNormalAttemptId is not null
            || row.FinalizationStatus is not null || row.FinalizationReason is not null
            || row.ProcessedAt is not null || row.ExecuteIntentAt is not null || row.OutcomeKind is not null
            || !await ReadTerminalNoStartProofAsync(resources, row.JobId, token).ConfigureAwait(false))
            return BackgroundJobCheckpointHistoryDisposition.Unresolved;
        return row.NoStartAt is not null ? BackgroundJobCheckpointHistoryDisposition.Committed
            : row.DeadLetteredAt is null ? BackgroundJobCheckpointHistoryDisposition.Absent
            : BackgroundJobCheckpointHistoryDisposition.Unresolved;
    }
}

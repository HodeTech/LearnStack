namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    private async Task<BackgroundJobProbeDisposition> ProbeExternalTerminalAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, ControlRow? row, CancellationToken token)
    {
        var claim = origin.TerminalClaim;
        var prepared = claim?.Prepared;
        if (claim is not null && !claim.MatchesProbe(origin)
            || origin.Profile != "external" || origin.Token == Guid.Empty || origin.Ordinal is null or < 0 or > 6
            || (prepared is null
                ? origin.ExpectedTerminalReason is not ("retired_definition" or "invalid_envelope" or "permanent_failure" or "retry_exhausted")
                : origin.ExpectedTerminalReason is not ("retired_definition" or "invalid_envelope" or "finalize_refused" or "finalize_exhausted"))
            || (origin.Ordinal == 0) != (origin.AttemptId is null)
            || origin.ExpectedTerminalReason == "retry_exhausted" && origin.Ordinal != 6
            || origin.ExpectedTerminalReason == "permanent_failure" && origin.Ordinal == 0)
            return BackgroundJobProbeDisposition.Unresolved;
        if (row is null) return BackgroundJobProbeDisposition.Unresolved;
        if (row.JobId != origin.JobId || row.TenantId != origin.TenantId || row.OrganizationId != origin.OrganizationId
            || row.DefinitionKey != origin.DefinitionKey || row.Queue != origin.Queue || row.CapacityGroup != origin.CapacityGroup
            || row.Profile != origin.Profile || row.WakeGeneration != origin.WakeGeneration
            || row.AttemptId != origin.AttemptId || row.NormalAttempts != origin.Ordinal)
            return BackgroundJobProbeDisposition.ChangedOwner;
        if (row.Phase != "terminal_pending" || row.TerminalReason != origin.ExpectedTerminalReason || row.ProcessedAt is not null)
            return BackgroundJobProbeDisposition.Unresolved;
        var eligibility = prepared is null ? BackgroundJobTerminalEligibility.NoPlan
            : "(" + BackgroundJobTerminalPreparedFacts.Fence + ") AND ("
                + (row.DeadLetteredAt is null ? BackgroundJobTerminalEligibility.PreparedBefore : BackgroundJobTerminalEligibility.PreparedAfter) + ")";
        var command = Command(resources, "SELECT TRUE FROM background_jobs j WHERE j.id=@id AND (" + eligibility + ")");
        command.Parameters.AddWithValue("id", origin.JobId);
        prepared?.AddParameters(command);
        var eligible = await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        if (!eligible) return BackgroundJobProbeDisposition.Unresolved;
        // A locked durable completion confirms the frozen request's exact facts,
        // never the original client's physical COMMIT acknowledgement or another owner.
        if (row.DeadLetteredAt is not null) return BackgroundJobProbeDisposition.TerminalCompleted;
        if (origin.Boundary == BackgroundJobCommitBoundary.Control && row.OwnershipToken is null
            && row.OwnershipKind is null && row.OwnershipUntil is null)
            return BackgroundJobProbeDisposition.ControlCommitted;
        if (row.OwnershipToken != origin.Token || row.OwnershipKind != "recovery")
            return BackgroundJobProbeDisposition.ChangedOwner;
        return origin.Boundary == BackgroundJobCommitBoundary.TerminalClaim
            ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.RolledBack;
    }
}

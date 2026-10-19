using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    /// <summary>Contain or pace unresolved intent; never infer outcome, quiescence or another normal admission.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> FinishExternalUnresolvedAsync(
        BackgroundJobExecutionIdentity identity, BackgroundJobExternalUnresolvedDisposition disposition,
        CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(identity);
        var origin = BackgroundJobCommitProbe.ForExternalUnresolved(identity);
        if (runtimeGate is null || !Enum.IsDefined(disposition) || !ValidUnresolvedOrigin(origin)
            || disposition is BackgroundJobExternalUnresolvedDisposition.ProbeFault or BackgroundJobExternalUnresolvedDisposition.ProbeTimeout
                && !identity.IsRecovery
            || disposition == BackgroundJobExternalUnresolvedDisposition.ReconcileRequired && identity.IsRecovery)
            throw new ArgumentException("Unresolved status requires its exact external normal owner and finite disposition.", nameof(identity));
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
                    && await ReadUnresolvedHistoryAsync(resources, origin, deadline.Token).ConfigureAwait(false))
                {
                    var status = await ReadUnresolvedStatusAsync(resources, origin.JobId, deadline.Token).ConfigureAwait(false);
                    if (status.Failures is >= 0 and < BackgroundJobRuntimeOptions.MaxUnsuccessfulRecoveryProbes
                        && status.QuarantineReason is null && row.OwnershipUntil > status.DbNow
                        && (identity.IsRecovery || identity.Deadline > status.DbNow))
                    {
                        var unsuccessful = disposition is BackgroundJobExternalUnresolvedDisposition.ProbeFault
                            or BackgroundJobExternalUnresolvedDisposition.ProbeTimeout;
                        var failures = status.Failures + (unsuccessful ? 1 : 0);
                        var reason = failures == BackgroundJobRuntimeOptions.MaxUnsuccessfulRecoveryProbes ? "probe_exhausted"
                            : disposition == BackgroundJobExternalUnresolvedDisposition.Unknown
                                || disposition == BackgroundJobExternalUnresolvedDisposition.Running
                                    && status.DbNow >= identity.Deadline + options.QuiescenceGrace ? "unknown_outcome" : null;
                        var token = identity.Token;
                        if (reason is null && !identity.IsRecovery)
                            token = await HandoffExternalNormalOwnerAsync(resources, origin, deadline.Token).ConfigureAwait(false);
                        if (token != Guid.Empty)
                        {
                            origin = origin with { PreviousUnsuccessfulProbes = status.Failures };
                            var changed = reason is not null
                                ? await QuarantineExternalAsync(resources, origin, failures, reason, deadline.Token).ConfigureAwait(false)
                                : await ParkUnresolvedExternalAsync(resources, origin, token, failures,
                                    disposition, deadline.Token).ConfigureAwait(false);
                            if (changed is not null)
                            {
                                value = changed.Value.Change;
                                if (value.JobId != origin.JobId || value.TerminalReason is not null
                                    || value.Phase != (reason is null ? "reconciling" : "quarantined")
                                    || value.WakeGeneration != checked(origin.WakeGeneration + (reason is null ? 1 : 0))
                                    || changed.Value.Failures != failures || changed.Value.Reason != reason
                                    || (changed.Value.ProbeAfter is null) != (reason is not null))
                                    throw new InvalidOperationException("Unresolved status returned a contradictory disposition.");
                                probes.Add(origin with
                                {
                                    ExpectedGeneration = value.WakeGeneration,
                                    ExpectedPhase = value.Phase,
                                    ExpectedProbeAfter = changed.Value.ProbeAfter,
                                    ExpectedUnsuccessfulProbes = changed.Value.Failures,
                                    ExpectedQuarantineReason = changed.Value.Reason
                                });
                                // This admits status accounting only. A closed process never
                                // obtains a new app/provider entry or revived Run here.
                                deadline.Token.ThrowIfCancellationRequested();
                                if (runtimeGate.TryAdmit()) await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(resources.CommitAcknowledged ? value : null, resources, probes.AsReadOnly());
    }

    private async Task<(BackgroundJobControlChange Change, DateTimeOffset? ProbeAfter, int Failures, string? Reason)?> ParkUnresolvedExternalAsync(
        BackgroundJobControlResources resources, BackgroundJobCommitProbe origin, Guid token, int failures,
        BackgroundJobExternalUnresolvedDisposition disposition, CancellationToken cancellationToken)
    {
        // Shortening and final parking are private to this transaction. Keep the
        // strong expiry-only wake guard; never publish the intermediate owner.
        var command = Command(resources, """
            WITH park_clock AS MATERIALIZED (SELECT clock_timestamp() AS at)
            UPDATE background_jobs j SET ownership_until=park_clock.at FROM park_clock WHERE
            """ + " " + ExternalNormalOwnerFence + """
             AND j.phase='reconciling' AND j.ownership_kind='recovery'
             AND j.ownership_until>park_clock.at AND j.unsuccessful_probes=@previous
            RETURNING j.ownership_until
            """);
        AddExternalNormalOwnerFence(command, origin, token);
        command.Parameters.AddWithValue("previous", origin.PreviousUnsuccessfulProbes!.Value);
        var shortened = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await resources.FinishCommandAsync().ConfigureAwait(false);
        if (shortened is not DateTime exactUntil) return null;
        command = Command(resources, """
            UPDATE background_jobs j SET ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,
                unsuccessful_probes=@failures,last_error=@error,
                probe_after=CASE WHEN @running THEN LEAST(clock_timestamp()+@pace,
                    CASE WHEN j.attempt_deadline>clock_timestamp() THEN j.attempt_deadline
                        ELSE j.attempt_deadline+@grace END) ELSE clock_timestamp()+@pace END
            WHERE
            """ + " " + ExternalNormalOwnerFence + """
             AND j.phase='reconciling' AND j.ownership_kind='recovery'
             AND j.ownership_until=@shortened AND j.ownership_until<=clock_timestamp()
             AND j.unsuccessful_probes=@previous
            RETURNING j.id,j.wake_generation,j.phase,j.terminal_reason,j.probe_after,j.unsuccessful_probes,j.quarantine_reason
            """);
        AddExternalNormalOwnerFence(command, origin, token);
        command.Parameters.AddWithValue("shortened", exactUntil);
        command.Parameters.AddWithValue("previous", origin.PreviousUnsuccessfulProbes.Value);
        command.Parameters.AddWithValue("failures", failures);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text,
            disposition is BackgroundJobExternalUnresolvedDisposition.ProbeFault or BackgroundJobExternalUnresolvedDisposition.ProbeTimeout
                ? "dependency_unavailable" : DBNull.Value);
        command.Parameters.AddWithValue("running", disposition == BackgroundJobExternalUnresolvedDisposition.Running);
        command.Parameters.AddWithValue("pace", options.RecoveryProbeInterval);
        command.Parameters.AddWithValue("grace", options.QuiescenceGrace);
        return await ReadUnresolvedChangeAsync(resources, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(BackgroundJobControlChange Change, DateTimeOffset? ProbeAfter, int Failures, string? Reason)?> QuarantineExternalAsync(
        BackgroundJobControlResources resources, BackgroundJobCommitProbe origin, int failures, string reason,
        CancellationToken cancellationToken)
    {
        var command = Command(resources, """
            UPDATE background_jobs j SET phase='quarantined',quarantine_reason=@reason,last_error='unknown_outcome',
                unsuccessful_probes=@failures,probe_after=NULL,ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL
            WHERE
            """ + " " + ExternalNormalOwnerFence + """
             AND j.phase=@phase AND j.ownership_kind=@kind AND j.ownership_until>clock_timestamp()
             AND (@recovered OR j.attempt_deadline>clock_timestamp()) AND j.unsuccessful_probes=@previous
            RETURNING j.id,j.wake_generation,j.phase,j.terminal_reason,j.probe_after,j.unsuccessful_probes,j.quarantine_reason
            """);
        AddExternalNormalOwnerFence(command, origin, origin.Token);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("failures", failures);
        command.Parameters.AddWithValue("previous", origin.PreviousUnsuccessfulProbes!.Value);
        command.Parameters.AddWithValue("phase", origin.IsRecovery ? "reconciling" : "executing");
        command.Parameters.AddWithValue("kind", origin.IsRecovery ? "recovery" : "execution");
        command.Parameters.AddWithValue("recovered", origin.IsRecovery);
        return await ReadUnresolvedChangeAsync(resources, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(BackgroundJobControlChange Change, DateTimeOffset? ProbeAfter, int Failures, string? Reason)?> ReadUnresolvedChangeAsync(
        BackgroundJobControlResources resources, CancellationToken token)
    {
        resources.SetReader(await resources.Command!.ExecuteReaderAsync(token).ConfigureAwait(false));
        (BackgroundJobControlChange, DateTimeOffset?, int, string?)? change = await resources.Reader!.ReadAsync(token).ConfigureAwait(false)
            ? (new(resources.Reader.GetGuid(0), resources.Reader.GetInt64(1), resources.Reader.GetString(2),
                resources.Reader.IsDBNull(3) ? null : resources.Reader.GetString(3)), TimeOrNull(resources.Reader, 4), resources.Reader.GetInt32(5),
                resources.Reader.IsDBNull(6) ? null : resources.Reader.GetString(6)) : null;
        await FinishAsync(resources).ConfigureAwait(false);
        return change;
    }

    private static bool ValidUnresolvedOrigin(BackgroundJobCommitProbe origin) =>
        origin.Boundary == BackgroundJobCommitBoundary.ExternalUnresolved && origin.Profile == "external"
        && origin.LedgerStage == BackgroundJobLedgerStage.Normal && origin.Ordinal is >= 1 and <= 6
        && origin.FinalizeOrdinal == 0 && origin.SelectedNormalAttemptId is null
        && origin.CapacityGroup is not null && origin.AttemptId is not null
        && origin.AdmittedAt is not null && origin.Deadline is not null && origin.OriginalDueAt is not null
        && origin.OriginalAdmissionToken is { } admission && admission != Guid.Empty && origin.Token != Guid.Empty
        && (origin.IsRecovery ? origin.Token != admission : origin.Token == admission);

    private async Task<bool> ReadUnresolvedHistoryAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, CancellationToken token)
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
                || row.NormalAttempts != origin.Ordinal || row.FinalizeAttempts != 0 || row.BusinessFinalizationIncomplete
                || row.JobSelectedNormalAttemptId is not null || row.FinalizationStatus is not null || row.FinalizationReason is not null
                || row.ProcessedAt is not null || row.DeadLetteredAt is not null || row.TerminalReason is not null
                || (i < rows.Count - 1 ? !PositiveRetryHistory(row)
                    : row.ExecuteIntentAt is null || row.NoStartAt is not null || row.OutcomeKind is not null || row.ReleasedAt is not null))
                return false;
        }
        var current = rows[^1];
        return current.AttemptId == origin.AttemptId && current.AdmissionToken == origin.OriginalAdmissionToken
            && current.AdmittedAt == origin.AdmittedAt && current.Deadline == origin.Deadline
            && current.CurrentAttemptDeadline == origin.Deadline;
    }

    private readonly record struct UnresolvedStatus(int Failures, DateTimeOffset? ProbeAfter, string? QuarantineReason, DateTimeOffset DbNow);

    private async Task<UnresolvedStatus> ReadUnresolvedStatusAsync(BackgroundJobControlResources resources, Guid job, CancellationToken token)
    {
        var command = Command(resources, "SELECT unsuccessful_probes,probe_after,quarantine_reason,clock_timestamp() FROM background_jobs WHERE id=@id");
        command.Parameters.AddWithValue("id", job);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        if (!await resources.Reader!.ReadAsync(token).ConfigureAwait(false)) throw BackgroundJobExternalCheckpointSql.Refused();
        var row = resources.Reader;
        var result = new UnresolvedStatus(row.GetInt32(0), TimeOrNull(row, 1), row.IsDBNull(2) ? null : row.GetString(2), row.GetFieldValue<DateTimeOffset>(3));
        await FinishAsync(resources).ConfigureAwait(false);
        return result;
    }

    private async Task<BackgroundJobProbeDisposition> ProbeUnresolvedExternalAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe origin, ControlRow? row, CancellationToken token)
    {
        if (!ValidUnresolvedOrigin(origin) || origin.PreviousUnsuccessfulProbes is not (>= 0 and < 6)
            || origin.ExpectedUnsuccessfulProbes is not (>= 0 and <= 6)
            || origin.ExpectedPhase is not ("reconciling" or "quarantined")
            || origin.ExpectedGeneration != origin.WakeGeneration + (origin.ExpectedPhase == "reconciling" ? 1 : 0)
            || (origin.ExpectedPhase == "reconciling"
                ? origin.ExpectedProbeAfter is null || origin.ExpectedQuarantineReason is not null
                : origin.ExpectedProbeAfter is not null || origin.ExpectedQuarantineReason is not ("unknown_outcome" or "probe_exhausted")))
            return BackgroundJobProbeDisposition.Unresolved;
        if (row is null) return BackgroundJobProbeDisposition.Unresolved;
        if (!MatchesKnownRetryRow(row, origin)) return BackgroundJobProbeDisposition.ChangedOwner;
        if (!await ReadUnresolvedHistoryAsync(resources, origin, token).ConfigureAwait(false))
            return BackgroundJobProbeDisposition.Unresolved;
        var status = await ReadUnresolvedStatusAsync(resources, origin.JobId, token).ConfigureAwait(false);
        if (MatchesKnownRetryOwner(row, origin) && status.Failures == origin.PreviousUnsuccessfulProbes
            && status.QuarantineReason is null) return BackgroundJobProbeDisposition.RolledBack;
        return row.WakeGeneration == origin.ExpectedGeneration && row.Phase == origin.ExpectedPhase
            && row.OwnershipToken is null && row.OwnershipKind is null && row.OwnershipUntil is null
            && status.Failures == origin.ExpectedUnsuccessfulProbes && status.ProbeAfter == origin.ExpectedProbeAfter
            && status.QuarantineReason == origin.ExpectedQuarantineReason
            ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.ChangedOwner;
    }
}

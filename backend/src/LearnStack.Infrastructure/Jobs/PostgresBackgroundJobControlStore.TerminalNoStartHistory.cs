using LearnStack.Application.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    /// <summary>
    /// Settles only the original acquisition's history. This produces no
    /// receipt, binding, current recovery authority or executable permission.
    /// </summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobProbeResult>> ProbeExternalTerminalNoStartAcquisitionAsync(
        OwnedBackgroundJobExternalRecovery origin, BackgroundJobCommitProbe probe, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(probe);
        origin.RequireDescriptor(probe);
        if (runtimeGate is null || !ReferenceEquals(origin.Runtime, runtimeGate.RunStop)
            || !origin.CleanupConfirmed || probe.Boundary != BackgroundJobCommitBoundary.ExternalRecovery
            || probe.ExpectedPhase != "terminal_pending" || probe.JobId.Version != 7 || probe.AttemptId?.Version != 7
            || probe.CapacityGroup is null || probe.Ordinal is not (>= 1 and <= 6)
            || probe.AdmittedAt is null || probe.Deadline is null || probe.OriginalDueAt is null
            || probe.Token == Guid.Empty || probe.RecoveryToken is not { } recovery
            || recovery == Guid.Empty || recovery == probe.Token
            || !TerminalNoStartReason(probe.ExpectedTerminalReason, probe.Ordinal))
            throw new ArgumentException("Terminal acquisition history requires its original cleaned operation and exact descriptor.", nameof(probe));

        var resources = new BackgroundJobControlResources { IsProbe = true };
        BackgroundJobProbeResult? value = null;
        using var deadline = Deadline(cancellationToken, probe: true);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false)
                && await LockExternalJobAsync(resources, probe.JobId, deadline.Token).ConfigureAwait(false))
            {
                var row = await LockRowAsync(resources, probe.JobId, deadline.Token).ConfigureAwait(false);
                var command = Command(resources, "SELECT clock_timestamp()");
                var now = (DateTime)(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false))!;
                await resources.FinishCommandAsync().ConfigureAwait(false);
                var dbNow = new DateTimeOffset(now);
                var disposition = BackgroundJobProbeDisposition.Unresolved;
                // Reuse positive cleanup history with the admission identity
                // actually captured by the original acquisition. Completion
                // requires the strict NoPlan/prepared after-state.
                var historyOrigin = probe with { OriginalAdmissionToken = probe.Token };
                if (row is not null && row.Phase == "terminal_pending" && row.ProcessedAt is null
                    && row.TerminalReason == probe.ExpectedTerminalReason
                    && MatchesNoPlanTerminalIdentity(row, historyOrigin)
                    && await ReadTerminalNoStartHistoryAsync(resources, historyOrigin,
                        probe.ExpectedTerminalReason, deadline.Token).ConfigureAwait(false))
                {
                    var noOwner = row.OwnershipToken is null && row.OwnershipKind is null && row.OwnershipUntil is null;
                    var recoveryOwner = row.OwnershipKind == "recovery" && row.OwnershipToken is { } actualToken
                        && actualToken != Guid.Empty && actualToken != probe.Token && row.OwnershipUntil is not null;
                    if (row.DeadLetteredAt is { } completed && completed <= dbNow && (noOwner || recoveryOwner))
                        disposition = BackgroundJobProbeDisposition.TerminalCompleted;
                    else if (row.DeadLetteredAt is null && recoveryOwner && row.OwnershipToken == recovery)
                        disposition = BackgroundJobProbeDisposition.ControlCommitted;
                    else if (row.DeadLetteredAt is null && (noOwner || recoveryOwner))
                        disposition = BackgroundJobProbeDisposition.ChangedOwner;
                    // In particular, a restored null owner does not assert the
                    // physical COMMIT rolled back: later legal transitions may
                    // share that shape. This is only settled old authority.
                }
                value = new(disposition, probe.JobId, row?.Phase, row?.WakeGeneration ?? 0, row?.NormalAttempts ?? 0,
                    row?.AttemptId, row?.OwnershipToken, row?.OwnershipUntil, row?.Deadline, dbNow);
                await resources.Transaction!.RollbackAsync(deadline.Token).ConfigureAwait(false);
                resources.ProbeRollbackAcknowledged = true;
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        if (!resources.ProbeRollbackAcknowledged || !resources.CleanupConfirmed || resources.Failure is not null) value = null;
        return new(value, resources, Array.Empty<BackgroundJobCommitProbe>(), (origin, probe));
    }
}

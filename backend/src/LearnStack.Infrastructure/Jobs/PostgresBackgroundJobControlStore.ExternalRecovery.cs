using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    private const string ExternalRecoveryProjection = """
        SELECT j.id,j.tenant_id,j.organization_id,j.definition_key,j.profile,j.queue,j.capacity_group,
            j.original_due_at,a.id,a.ordinal,a.admitted_at,a.deadline,a.admission_token,
            j.wake_generation,j.ownership_token,j.ownership_until,clock_timestamp(),a.stage,j.phase,j.terminal_reason
        FROM background_jobs j JOIN background_job_attempts a
            ON a.tenant_id=j.tenant_id AND a.job_id=j.id AND a.id=j.current_attempt_id
                AND a.organization_id IS NOT DISTINCT FROM j.organization_id
        WHERE j.id=@id AND j.profile='external'
            AND j.capacity_group IS NOT NULL AND a.stage='normal' AND a.ordinal=j.normal_attempts
            AND a.deadline=j.attempt_deadline AND j.wake_generation=@generation
            AND j.ownership_kind='recovery' AND j.ownership_token=@recovery
            AND j.ownership_token<>a.admission_token AND j.ownership_until>clock_timestamp()
            AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL
            AND ((j.phase='reconciling' AND j.terminal_reason IS NULL)
                OR (
        """ + BackgroundJobTerminalEligibility.TerminalNoStartBefore + "))";

    /// <summary>Called only by the charged external invocation, never the control loop.</summary>
    internal async Task<OwnedBackgroundJobExternalRecovery> AcquireExternalRecoveryAsync(
        BackgroundJobId job, long generation, string queue, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        if (job.Value.Version != 7 || generation < 1 || !BackgroundJobNames.QueueOrPool(queue) || runtimeGate is null)
            throw new ArgumentException("Recovery requires a native invocation and its process boundary.");
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        ExternalRecoveryCandidate? candidate = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false)
                && await LockExternalJobAsync(resources, job.Value, deadline.Token).ConfigureAwait(false))
            {
                var row = await LockRowAsync(resources, job.Value, deadline.Token).ConfigureAwait(false);
                if (row is
                    {
                        Profile: "external", Phase: "reconciling", CapacityGroup: not null,
                        LedgerAdmittedAt: not null
                    } && row.MatchingNormalAttempt && row.Incomplete
                    && row.WakeGeneration == generation && row.Queue == queue
                    && row.FinalizeAttempts == 0 && row.SelectedNormalAttemptId is null
                    && definitions.Resolve(row.DefinitionKey) is
                    {
                        Disposition: BackgroundJobDefinitionDisposition.Active,
                        Definition: { Profile: BackgroundJobExecutionProfile.External } definition
                    }
                    && definition.Queue == row.Queue && definition.CapacityGroup == row.CapacityGroup
                    && (definition.Scope == BackgroundJobScope.TenantWide) == (row.OrganizationId is null))
                {
                    var recovery = ids.NewUuidV4();
                    var command = Command(resources, """
                        UPDATE background_jobs SET ownership_token=@recovery,ownership_kind='recovery',
                            ownership_until=clock_timestamp()+@lease,probe_after=NULL
                        WHERE id=@id AND profile='external' AND phase='reconciling'
                            AND wake_generation=@generation AND current_attempt_id=@attempt
                            AND ownership_token IS NOT DISTINCT FROM @token
                            AND (ownership_token IS NULL OR ownership_until<=clock_timestamp())
                            AND ownership_token IS NULL AND probe_after IS NOT NULL AND probe_after<=clock_timestamp()
                            AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
                        """);
                    AddRowFence(command, row);
                    command.Parameters.AddWithValue("recovery", recovery);
                    command.Parameters.AddWithValue("lease", options.LeaseDuration);
                    var acquired = await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) == 1;
                    await resources.FinishCommandAsync().ConfigureAwait(false);
                    if (acquired)
                    {
                        candidate = await ReadExternalRecoveryAsync(resources, row.JobId, generation, recovery, deadline.Token).ConfigureAwait(false);
                        probes.Add(new(BackgroundJobCommitBoundary.ExternalRecovery, row.JobId, row.TenantId,
                            row.OrganizationId, row.DefinitionKey, row.Queue, row.WakeGeneration, row.AttemptId,
                            row.LedgerToken!.Value, CapacityGroup: row.CapacityGroup, RecoveryToken: recovery,
                            Ordinal: row.NormalAttempts, Deadline: row.Deadline, AdmittedAt: row.LedgerAdmittedAt,
                            OriginalDueAt: row.OriginalDueAt, ExpectedPhase: "reconciling"));
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

    internal async Task<OwnedBackgroundJobControl<BackgroundJobExternalRecoveryReceipt>> ProbeExternalRecoveryAsync(
        OwnedBackgroundJobExternalRecovery origin, BackgroundJobCommitProbe probe, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(origin);
        origin.RequireDescriptor(probe);
        if (probe.Boundary != BackgroundJobCommitBoundary.ExternalRecovery || runtimeGate is null
            || !ReferenceEquals(origin.Runtime, runtimeGate.RunStop)
            || probe.AttemptId is null || probe.RecoveryToken is null || probe.AdmittedAt is null
            || probe.Deadline is null || probe.OriginalDueAt is null
            || !(probe.ExpectedPhase == "reconciling" && probe.ExpectedTerminalReason is null
                || probe.ExpectedPhase == "terminal_pending" && TerminalNoStartReason(probe.ExpectedTerminalReason, probe.Ordinal)))
            throw new ArgumentException("External recovery confirmation requires its exact acquisition descriptor.", nameof(probe));
        var resources = new BackgroundJobControlResources { IsProbe = true };
        ExternalRecoveryCandidate? candidate = null;
        using var deadline = Deadline(cancellationToken, probe: true);
        try
        {
            if (origin.CleanupConfirmed && await OpenAsync(resources, deadline.Token).ConfigureAwait(false)
                && await LockExternalJobAsync(resources, probe.JobId, deadline.Token).ConfigureAwait(false))
            {
                // The fresh statement follows actual lock acquisition. Neither
                // a skipped row nor an old joined ledger is a rollback proof.
                var row = await LockRowAsync(resources, probe.JobId, deadline.Token).ConfigureAwait(false);
                if (row is { Profile: "external", OwnershipKind: "recovery", ProcessedAt: null, DeadLetteredAt: null }
                    && row.Phase == probe.ExpectedPhase && row.TerminalReason == probe.ExpectedTerminalReason
                    && row.MatchingNormalAttempt && row.JobId == probe.JobId
                    && row.TenantId == probe.TenantId && row.OrganizationId == probe.OrganizationId
                    && row.DefinitionKey == probe.DefinitionKey && row.Queue == probe.Queue
                    && row.CapacityGroup == probe.CapacityGroup && row.OriginalDueAt == probe.OriginalDueAt
                    && row.AttemptId == probe.AttemptId && row.NormalAttempts == probe.Ordinal
                    && row.LedgerAdmittedAt == probe.AdmittedAt && row.Deadline == probe.Deadline
                    && row.LedgerToken == probe.Token && row.WakeGeneration == probe.WakeGeneration
                    && row.OwnershipToken == probe.RecoveryToken
                    && (row.Phase == "reconciling" || await ReadTerminalNoStartHistoryAsync(resources,
                        NoPlanHistoryOrigin(row), row.TerminalReason, deadline.Token).ConfigureAwait(false)))
                    candidate = await ReadExternalRecoveryAsync(resources, row.JobId, probe.WakeGeneration,
                        probe.RecoveryToken.Value, deadline.Token).ConfigureAwait(false);
                await resources.Transaction!.RollbackAsync(deadline.Token).ConfigureAwait(false);
                resources.ProbeRollbackAcknowledged = true;
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        var receipt = candidate is not null && resources.ProbeRollbackAcknowledged && resources.CleanupConfirmed && resources.Failure is null
            ? origin.PublishConfirmation(probe, PublishExternalRecoveryCandidate(candidate)) : null;
        return new(receipt, resources, Array.Empty<BackgroundJobCommitProbe>(), (origin, candidate));
    }

    private async Task<bool> LockExternalJobAsync(BackgroundJobControlResources resources, Guid job, CancellationToken token)
    {
        var command = Command(resources, "SELECT id FROM background_jobs WHERE id=@id FOR UPDATE SKIP LOCKED");
        command.Parameters.AddWithValue("id", job);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        var locked = await resources.Reader!.ReadAsync(token).ConfigureAwait(false);
        await FinishAsync(resources).ConfigureAwait(false);
        return locked;
    }

    private async Task<ExternalRecoveryCandidate?> ReadExternalRecoveryAsync(BackgroundJobControlResources resources,
        Guid job, long generation, Guid recovery, CancellationToken token)
    {
        var command = Command(resources, ExternalRecoveryProjection);
        command.Parameters.AddWithValue("id", job);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("recovery", recovery);
        var observedAt = Stopwatch.GetTimestamp();
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        var candidate = await resources.Reader!.ReadAsync(token).ConfigureAwait(false)
            ? CaptureExternalRecoveryCandidate(resources.Reader, runtimeGate!.RunStop, observedAt) : null;
        await FinishAsync(resources).ConfigureAwait(false);
        return candidate;
    }
}

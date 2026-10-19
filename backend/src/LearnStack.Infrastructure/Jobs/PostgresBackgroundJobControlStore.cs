using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Time;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Payload-free short transactional control; no operation grants execution authority.</summary>
internal sealed partial class PostgresBackgroundJobControlStore(PrivilegedDataSource source,
    BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeOptions options, IGuidFactory ids,
    BackgroundJobRuntimeGate? runtimeGate = null) : BackgroundJobCapacityRecoveryReceipt.MintingSource
{
    private const string Incomplete = "processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL";

    internal async Task<OwnedBackgroundJobControl<IReadOnlyList<BackgroundJobRenewal>>> RenewBatchAsync(
        IReadOnlyList<BackgroundJobExecutionIdentity> identities, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(identities);
        var frozen = identities.ToArray();
        if (frozen.Length > 64 || frozen.Any(identity => identity is null || !Enum.IsDefined(identity.LedgerStage))
            || frozen.Select(identity => identity.JobId.Value).Distinct().Count() != frozen.Length)
            throw new ArgumentException("Renewal requires at most 64 distinct acknowledged invocations.", nameof(identities));
        if (runtimeGate is null && frozen.Any(identity => identity.IsRecovery))
            throw new ArgumentException("Recovery renewal requires its exact process boundary.", nameof(identities));
        options.Validate();
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        IReadOnlyList<BackgroundJobRenewal>? value = null;
        var recoveryCandidates = new Dictionary<BackgroundJobExecutionIdentity, ExternalRecoveryRenewalCandidate>();
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (frozen.Length == 0 || !await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
                value = Array.Empty<BackgroundJobRenewal>();
            else
            {
                var locked = new List<Guid>();
                var command = Command(resources, "SELECT id FROM background_jobs WHERE id = ANY(@ids) ORDER BY id FOR NO KEY UPDATE SKIP LOCKED");
                command.Parameters.AddWithValue("ids", frozen.Select(identity => identity.JobId.Value).ToArray());
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                while (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false)) locked.Add(resources.Reader.GetGuid(0));
                await FinishAsync(resources).ConfigureAwait(false);

                command = Command(resources, """
                    WITH renewal_clock AS MATERIALIZED (SELECT clock_timestamp() AS at),
                    input AS (SELECT * FROM unnest(@ids, @tenants, @organizations, @definitions, @queues,
                        @attempts, @tokens, @generations, @deadlines, @ordinals, @groups,
                        @profiles, @stages, @finalOrdinals, @selected, @admitted, @due, @kinds, @admissionTokens)
                        AS x(id, tenant, organization, definition, queue, attempt, token, generation, deadline, ordinal, capacity,
                            profile, stage, final_ordinal, selected, admitted, due, kind, admission_token))
                    UPDATE background_jobs j SET ownership_until = CASE WHEN x.kind='recovery'
                        THEN renewal_clock.at + @lease ELSE LEAST(renewal_clock.at + @lease, j.attempt_deadline) END
                    FROM input x, renewal_clock
                    WHERE j.id = x.id AND j.id = ANY(@locked) AND j.tenant_id = x.tenant
                        AND j.organization_id IS NOT DISTINCT FROM x.organization
                        AND j.definition_key = x.definition AND j.queue = x.queue
                        AND j.profile = x.profile AND j.capacity_group IS NOT DISTINCT FROM x.capacity
                        AND j.original_due_at=x.due
                        AND j.current_attempt_id = x.attempt AND j.ownership_token = x.token
                        AND j.wake_generation = x.generation AND j.normal_attempts = x.ordinal
                        AND j.finalize_attempts=x.final_ordinal AND j.final_outcome_attempt_id IS NOT DISTINCT FROM x.selected
                        AND j.attempt_deadline = x.deadline AND j.ownership_kind = x.kind
                        AND ((x.kind='execution' AND x.admission_token=x.token AND ((x.profile='transactional' AND x.stage='normal' AND j.phase='executing'
                                AND x.final_ordinal=0 AND x.selected IS NULL)
                            OR (x.profile='external' AND (
                                (x.stage='normal' AND j.phase IN ('preparing','prepared','executing')
                                    AND x.final_ordinal=0 AND x.selected IS NULL)
                                OR (x.stage='finalize' AND j.phase='finalizing' AND x.final_ordinal BETWEEN 1 AND 6
                                    AND x.selected IS NOT NULL)))))
                            OR (x.kind='recovery' AND x.profile='external' AND x.stage='normal' AND j.phase='reconciling'
                                AND x.final_ordinal=0 AND x.selected IS NULL AND x.token<>x.admission_token))
                        AND EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.id=x.attempt
                            AND a.tenant_id=x.tenant AND a.job_id=x.id
                            AND a.organization_id IS NOT DISTINCT FROM x.organization
                            AND a.stage=x.stage AND a.ordinal=CASE WHEN x.stage='normal' THEN x.ordinal ELSE x.final_ordinal END
                            AND a.admission_token=x.admission_token AND a.admitted_at=x.admitted AND a.deadline=x.deadline
                            AND a.final_outcome_attempt_id IS NOT DISTINCT FROM x.selected)
                        AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL AND j.terminal_reason IS NULL
                        AND j.ownership_until > renewal_clock.at
                        AND (x.kind='recovery' OR j.attempt_deadline > renewal_clock.at)
                    RETURNING j.id,j.tenant_id,j.organization_id,j.definition_key,j.profile,j.queue,j.capacity_group,
                        j.original_due_at,j.current_attempt_id,j.normal_attempts,x.admitted,j.attempt_deadline,
                        x.admission_token,j.wake_generation,j.ownership_token,j.ownership_until,renewal_clock.at,x.stage
                    """);
                command.Parameters.AddWithValue("ids", frozen.Select(identity => identity.JobId.Value).ToArray());
                command.Parameters.AddWithValue("tenants", frozen.Select(identity => identity.TenantId.Value).ToArray());
                command.Parameters.AddWithValue("organizations", NpgsqlDbType.Array | NpgsqlDbType.Uuid, frozen.Select(identity => identity.OrganizationId?.Value).ToArray());
                command.Parameters.AddWithValue("definitions", frozen.Select(identity => identity.DefinitionKey).ToArray());
                command.Parameters.AddWithValue("queues", frozen.Select(identity => identity.Queue).ToArray());
                command.Parameters.AddWithValue("attempts", frozen.Select(identity => identity.AttemptId.Value).ToArray());
                command.Parameters.AddWithValue("tokens", frozen.Select(identity => identity.Token).ToArray());
                command.Parameters.AddWithValue("generations", frozen.Select(identity => identity.WakeGeneration).ToArray());
                command.Parameters.AddWithValue("deadlines", frozen.Select(identity => identity.Deadline).ToArray());
                command.Parameters.AddWithValue("ordinals", frozen.Select(identity => identity.NormalAttemptOrdinal).ToArray());
                command.Parameters.AddWithValue("groups", NpgsqlDbType.Array | NpgsqlDbType.Text, frozen.Select(identity => identity.CapacityGroup).ToArray());
                command.Parameters.AddWithValue("profiles", frozen.Select(identity => identity.Profile).ToArray());
                command.Parameters.AddWithValue("stages", frozen.Select(identity => identity.LedgerStage == BackgroundJobLedgerStage.Normal ? "normal" : "finalize").ToArray());
                command.Parameters.AddWithValue("finalOrdinals", frozen.Select(identity => identity.FinalizeAttemptOrdinal).ToArray());
                command.Parameters.AddWithValue("selected", NpgsqlDbType.Array | NpgsqlDbType.Uuid, frozen.Select(identity => identity.SelectedNormalAttemptId?.Value).ToArray());
                command.Parameters.AddWithValue("admitted", frozen.Select(identity => identity.AdmittedAt).ToArray());
                command.Parameters.AddWithValue("due", frozen.Select(identity => identity.OriginalDueAt).ToArray());
                command.Parameters.AddWithValue("kinds", frozen.Select(identity => identity.IsRecovery ? "recovery" : "execution").ToArray());
                command.Parameters.AddWithValue("admissionTokens", frozen.Select(identity => identity.AdmissionToken).ToArray());
                command.Parameters.AddWithValue("locked", locked.ToArray());
                command.Parameters.AddWithValue("lease", options.LeaseDuration);
                var renewals = new List<BackgroundJobRenewal>();
                var observedAt = Stopwatch.GetTimestamp();
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                while (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    var identity = frozen.Single(item => item.JobId.Value == resources.Reader.GetGuid(0));
                    renewals.Add(new(identity, resources.Reader.GetFieldValue<DateTimeOffset>(15), resources.Reader.GetFieldValue<DateTimeOffset>(16)));
                    if (identity.IsRecovery)
                        recoveryCandidates.Add(identity, CaptureExternalRecoveryRenewalCandidate(resources.Reader, runtimeGate!.RunStop, observedAt));
                }
                await FinishAsync(resources).ConfigureAwait(false);
                probes.AddRange(frozen.Select(identity => BackgroundJobCommitProbe.ForRenewal(identity,
                    renewals.SingleOrDefault(renewal => ReferenceEquals(renewal.Identity, identity))?.OwnershipUntil)));
                await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                value = renewals.AsReadOnly();
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        if (value is not null && resources.CommitAcknowledged && resources.CleanupConfirmed && resources.Failure is null)
            value = value.Select(row => recoveryCandidates.TryGetValue(row.Identity, out var candidate)
                ? row with { RecoveryLease = PublishExternalRecoveryRenewalCandidate(candidate) } : row).ToArray();
        return new(value, resources, probes.AsReadOnly(), recoveryCandidates);
    }

    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> FinishKnownRollbackAsync(
        BackgroundJobExecutionIdentity identity, BackgroundJobFailureDecision decision, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Disposition is not (BackgroundJobFailureDisposition.Retry or BackgroundJobFailureDisposition.Terminal)
            || decision.ErrorCode is not ("concurrency_conflict" or "rate_limited" or "dependency_unavailable" or "audit_unavailable" or "unexpected_fault" or "permanent_failure"))
            throw new ArgumentException("Known rollback control requires a closed retry or terminal decision.", nameof(decision));
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        BackgroundJobControlChange? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var row = await LockRowAsync(resources, identity.JobId.Value, deadline.Token).ConfigureAwait(false);
                if (identity.CapacityGroup is null && row is not null && row.Matches(identity) && row.Phase == "executing" && row.OwnershipKind == "execution"
                    && row.Incomplete && row.MatchingAttempt && row.LedgerToken == identity.Token)
                {
                    var command = Command(resources, """
                        UPDATE background_jobs SET ownership_until = clock_timestamp()
                        WHERE id = @id AND current_attempt_id = @attempt AND ownership_token = @token
                            AND wake_generation = @generation AND phase = 'executing' AND ownership_kind = 'execution'
                            AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
                            AND ownership_until > clock_timestamp()
                        """);
                    AddRowFence(command, row);
                    await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                    await resources.FinishCommandAsync().ConfigureAwait(false);
                    var reason = decision.Disposition == BackgroundJobFailureDisposition.Terminal
                        ? BackgroundJobTerminalReason.PermanentFailure : (BackgroundJobTerminalReason?)null;
                    value = await RecoverAsync(resources, row, reason, decision.ErrorCode, deadline.Token).ConfigureAwait(false);
                    if (value is not null) probes.Add(Probe(row, value));
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(resources.CommitAcknowledged ? value : null, resources, probes.AsReadOnly());
    }

    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> RequestInvalidEnvelopeAsync(
        BackgroundJobRoutingClaim claim, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(claim);
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        BackgroundJobControlChange? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var row = await LockRowAsync(resources, claim.JobId.Value, deadline.Token).ConfigureAwait(false);
                if (row is not null && row.Matches(claim) && row.OwnershipKind == "routing" && row.Incomplete)
                {
                    if (row.Profile == "external")
                    {
                        var requested = await RequestExternalUninvokableAsync(resources, row,
                            BackgroundJobTerminalReason.InvalidEnvelope, claim, deadline.Token).ConfigureAwait(false);
                        if (requested is { } external) { value = external.Change; probes.Add(external.Probe); }
                    }
                    else if (row.Profile == "transactional" && claim.Profile == BackgroundJobExecutionProfile.Transactional
                        && claim.LedgerStage == BackgroundJobLedgerStage.Normal && row.Phase == "pending")
                    {
                        value = await RequestTerminalAsync(resources, row, BackgroundJobTerminalReason.InvalidEnvelope,
                            " AND ownership_kind = 'routing' AND ownership_until > clock_timestamp()", deadline.Token).ConfigureAwait(false);
                        if (value is not null) probes.Add(Probe(row, value));
                    }
                    if (value is not null) await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(resources.CommitAcknowledged ? value : null, resources, probes.AsReadOnly());
    }

    internal async Task<OwnedBackgroundJobControl<IReadOnlyList<BackgroundJobControlChange>>> RunRecoveryQuantumAsync(
        BackgroundJobControlLane lane, IReadOnlyCollection<Guid> retainedJobIds, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(retainedJobIds);
        if (!Enum.IsDefined(lane) || retainedJobIds.Count > BackgroundJobControlLimits.MaximumRetainedIds) throw new ArgumentException("A control quantum requires its finite lane and bounded retained inventory.");
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        IReadOnlyList<BackgroundJobControlChange>? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var active = definitions.DefinitionKeys.Where(key => definitions.TryGet(key, out var definition)
                    && definition.Readiness == BackgroundJobDefinitionReadiness.Active
                    && definition.Profile == BackgroundJobExecutionProfile.Transactional).ToArray();
                var compatible = active.Concat(definitions.RetiredKeys).ToArray();
                var external = definitions.DefinitionKeys.Select(key => definitions.Resolve(key))
                    .Where(resolution => resolution.Disposition == BackgroundJobDefinitionDisposition.Active
                        && resolution.Definition!.Profile == BackgroundJobExecutionProfile.External)
                    .Select(resolution => resolution.Definition!).ToArray();
                var command = Command(resources, SelectionSql(lane));
                command.Parameters.AddWithValue("blocked", retainedJobIds.ToArray());
                command.Parameters.AddWithValue("compatible", compatible);
                command.Parameters.AddWithValue("retired", definitions.RetiredKeys.ToArray());
                command.Parameters.AddWithValue("batch", options.ControlBatchSize);
                command.Parameters.AddWithValue("externalDefinitions", external.Select(definition => definition.Key).ToArray());
                command.Parameters.AddWithValue("externalQueues", external.Select(definition => definition.Queue).ToArray());
                command.Parameters.AddWithValue("externalGroups", external.Select(definition => definition.CapacityGroup!).ToArray());
                command.Parameters.AddWithValue("externalTenantWide", external.Select(definition => definition.Scope == BackgroundJobScope.TenantWide).ToArray());
                var selected = new List<Guid>();
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                while (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false)) selected.Add(resources.Reader.GetGuid(0));
                await FinishAsync(resources).ConfigureAwait(false);
                var changes = new List<BackgroundJobControlChange>();
                foreach (var id in selected)
                {
                    var row = await LockRowAsync(resources, id, deadline.Token).ConfigureAwait(false);
                    if (row is null || !row.Incomplete) continue;
                    // Routing grants no business admission. Expired routing in
                    // either profile must re-enable its same-generation bridge.
                    if (lane == BackgroundJobControlLane.Recovery && row.OwnershipKind == "routing"
                        && row.Phase is "pending" or "finalize_pending")
                    {
                        var cleared = await ClearExpiredRoutingAsync(resources, row, deadline.Token).ConfigureAwait(false);
                        if (cleared is not null) { changes.Add(cleared); probes.Add(Probe(row, cleared)); }
                        continue;
                    }
                    // Finalize owns no reservation. It cannot use the generic
                    // transactional capacity-recovery path below.
                    if (lane == BackgroundJobControlLane.ExternalFinalize
                        || lane == BackgroundJobControlLane.Exhaustion && row.Profile == "external")
                    {
                        var finalized = await RecoverExternalFinalizeAsync(resources, row, deadline.Token).ConfigureAwait(false);
                        if (finalized is { } disposed) { changes.Add(disposed.Change); probes.Add(disposed.Probe); }
                        continue;
                    }
                    var resolution = definitions.Resolve(row.DefinitionKey);
                    if (lane == BackgroundJobControlLane.Retirement && row.Profile == "external")
                    {
                        var requested = await RequestExternalUninvokableAsync(resources, row,
                            BackgroundJobTerminalReason.RetiredDefinition, null, deadline.Token).ConfigureAwait(false);
                        if (requested is { } retired) { changes.Add(retired.Change); probes.Add(retired.Probe); }
                        continue;
                    }
                    var externalLane = lane is BackgroundJobControlLane.ExternalRecovery or BackgroundJobControlLane.ExternalUnarmed;
                    if (resolution is { Disposition: BackgroundJobDefinitionDisposition.Active, Definition: { } definition }
                        && (definition.Profile != (externalLane ? BackgroundJobExecutionProfile.External : BackgroundJobExecutionProfile.Transactional)
                            || definition.CapacityGroup != row.CapacityGroup
                            || definition.Queue != row.Queue
                            || (definition.Scope == BackgroundJobScope.TenantWide ? row.OrganizationId is not null : row.OrganizationId is null)))
                        continue;
                    BackgroundJobControlChange? changed = null;
                    if (externalLane && resolution.Disposition == BackgroundJobDefinitionDisposition.Active)
                        changed = await ParkExternalRecoveryAsync(resources, row,
                            lane == BackgroundJobControlLane.ExternalUnarmed, deadline.Token).ConfigureAwait(false);
                    else if (lane == BackgroundJobControlLane.CapacityProbe && row.Phase == "reconciling" && row.OwnershipToken is null)
                        changed = await RecoverCapacityRowAsync(resources, row,
                            resolution.Disposition == BackgroundJobDefinitionDisposition.Retired ? BackgroundJobTerminalReason.RetiredDefinition : null,
                            "unexpected_fault", deadline.Token).ConfigureAwait(false);
                    else if (resolution.Disposition is BackgroundJobDefinitionDisposition.Active or BackgroundJobDefinitionDisposition.Retired)
                    {
                        var reason = resolution.Disposition == BackgroundJobDefinitionDisposition.Retired
                            ? BackgroundJobTerminalReason.RetiredDefinition : (BackgroundJobTerminalReason?)null;
                        if (lane == BackgroundJobControlLane.Recovery && row.MatchingAttempt
                            && row.Phase is "executing" or "reconciling" && row.OwnershipKind is "execution" or "recovery")
                            changed = await RecoverAsync(resources, row, reason, "unexpected_fault", deadline.Token).ConfigureAwait(false);
                        else if (row.Phase == "pending" && row.OwnershipToken is null)
                            changed = await RequestTerminalAsync(resources, row, reason ?? BackgroundJobTerminalReason.RetryExhausted,
                                " AND ownership_token IS NULL AND available_after <= clock_timestamp()", deadline.Token).ConfigureAwait(false);
                    }
                    if (changed is not null) { changes.Add(changed); probes.Add(Probe(row, changed)); }
                }
                await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                value = changes.AsReadOnly();
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, probes.AsReadOnly());
    }

    internal async Task<OwnedBackgroundJobControl<BackgroundJobProbeResult>> ProbeCommitAsync(
        BackgroundJobCommitProbe probe, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(probe);
        var resources = new BackgroundJobControlResources();
        BackgroundJobProbeResult? value = null;
        Candidate? recoveryCandidate = null;
        resources.IsProbe = true;
        using var deadline = Deadline(cancellationToken, probe: true);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                // This lock serializes with the original transaction even when
                // it is still holding the row before an unresolved physical COMMIT.
                var row = await LockRowAsync(resources, probe.JobId, deadline.Token).ConfigureAwait(false);
                var command = Command(resources, "SELECT clock_timestamp()");
                var now = (DateTime)(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false))!;
                await resources.FinishCommandAsync().ConfigureAwait(false);
                var dbNow = new DateTimeOffset(now);
                var capacity = probe.FinalizationHistory is null && probe.TerminalClaim is null && probe.UninvokableHistory is null
                    && probe.Boundary is BackgroundJobCommitBoundary.CapacityRecovery or BackgroundJobCommitBoundary.TransactionalCleanup
                    ? await ProbeCapacityAsync(resources, probe, row, dbNow, deadline.Token).ConfigureAwait(false) : null;
                // A descriptor cannot switch its original history by carrying
                // a second specialized proof from another operation or job.
                var multipleHistories = (probe.FinalizationHistory is null ? 0 : 1)
                    + (probe.TerminalClaim is null ? 0 : 1) + (probe.UninvokableHistory is null ? 0 : 1) > 1;
                var disposition = multipleHistories ? BackgroundJobProbeDisposition.Unresolved
                    : probe.FinalizationHistory is not null
                    ? await ProbeExternalFinalizeAsync(resources, probe, row, deadline.Token).ConfigureAwait(false)
                    : probe.TerminalClaim is not null
                    ? await ProbeExternalTerminalAsync(resources, probe, row, deadline.Token).ConfigureAwait(false)
                    : probe.UninvokableHistory is not null || probe.Boundary == BackgroundJobCommitBoundary.ExternalUninvokableTerminalRequest
                    ? await ProbeExternalUninvokableRequestAsync(resources, probe, row, deadline.Token).ConfigureAwait(false)
                    : (probe.Profile == "external" || row?.Profile == "external")
                    && (probe.Boundary is BackgroundJobCommitBoundary.TerminalClaim or BackgroundJobCommitBoundary.Terminal
                        || probe.Boundary == BackgroundJobCommitBoundary.Control && probe.ExpectedPhase == "terminal_pending")
                    ? await ProbeExternalTerminalAsync(resources, probe, row, deadline.Token).ConfigureAwait(false)
                    : probe.Boundary == BackgroundJobCommitBoundary.ExternalFinalizeDisposition
                    ? await ProbeExternalFinalizeAsync(resources, probe, row, deadline.Token).ConfigureAwait(false)
                    : probe.Boundary == BackgroundJobCommitBoundary.ExternalNoPlanTerminalRequest
                    ? await ProbeNoPlanTerminalRequestAsync(resources, probe, row, deadline.Token).ConfigureAwait(false)
                    : probe.Boundary == BackgroundJobCommitBoundary.ExternalKnownRetry
                    ? await ProbeKnownExternalRetryAsync(resources, probe, row, deadline.Token).ConfigureAwait(false)
                    : probe.Boundary == BackgroundJobCommitBoundary.ExternalUnresolved
                    ? await ProbeUnresolvedExternalAsync(resources, probe, row, deadline.Token).ConfigureAwait(false)
                    : capacity?.Disposition ?? (probe.Boundary == BackgroundJobCommitBoundary.Admission
                    ? await ProbeAdmissionAsync(resources, probe, row, deadline.Token).ConfigureAwait(false) : Interpret(probe, row));
                value = new(disposition, probe.JobId, row?.Phase, row?.WakeGeneration ?? 0, row?.NormalAttempts ?? 0,
                    row?.AttemptId, row?.OwnershipToken, row?.OwnershipUntil, row?.Deadline, dbNow)
                { CurrentRecoveryAuthority = capacity?.CurrentAuthority == true };
                recoveryCandidate = capacity?.Candidate;
                // A read probe needs no COMMIT and cannot create recursive COMMIT uncertainty.
                await resources.Transaction!.RollbackAsync(deadline.Token).ConfigureAwait(false);
                resources.ProbeRollbackAcknowledged = true;
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        if (!resources.ProbeRollbackAcknowledged || !resources.CleanupConfirmed || resources.Failure is not null) value = null;
        else if (value is not null && recoveryCandidate is not null)
            value = value with { RecoveryReceipt = ConfirmCandidate(recoveryCandidate) };
        return new(value, resources, Array.Empty<BackgroundJobCommitProbe>(), recoveryCandidate);
    }

    private async Task<BackgroundJobProbeDisposition> ProbeAdmissionAsync(BackgroundJobControlResources resources,
        BackgroundJobCommitProbe probe, ControlRow? row, CancellationToken token)
    {
        if (row is null) return BackgroundJobProbeDisposition.Missing;
        if (row.TenantId != probe.TenantId || row.OrganizationId != probe.OrganizationId
            || row.DefinitionKey != probe.DefinitionKey || row.Queue != probe.Queue
            || row.CapacityGroup != probe.CapacityGroup || row.Profile != "transactional")
            return BackgroundJobProbeDisposition.ChangedOwner;
        // The original transaction has finished before this job lock is held.
        // A peer may have cleared or replaced routing meanwhile; only a fresh
        // read of immutable history can prove the original admission absent.
        var command = Command(resources, """
            SELECT id FROM background_job_attempts
                WHERE tenant_id=@tenant AND job_id=@id AND stage='normal' AND admission_token=@original
                    AND organization_id IS NOT DISTINCT FROM @organization
            """);
        command.Parameters.AddWithValue("tenant", probe.TenantId);
        command.Parameters.AddWithValue("id", probe.JobId);
        command.Parameters.AddWithValue("original", probe.Token);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)probe.OrganizationId ?? DBNull.Value);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        var attempt = await resources.Reader!.ReadAsync(token).ConfigureAwait(false) ? resources.Reader.GetGuid(0) : (Guid?)null;
        var ambiguous = await resources.Reader.ReadAsync(token).ConfigureAwait(false);
        await FinishAsync(resources).ConfigureAwait(false);
        if (ambiguous) return BackgroundJobProbeDisposition.Unresolved;
        if (attempt is null) return BackgroundJobProbeDisposition.RolledBack;
        if (row.CapacityGroup is not null
            && (row.AttemptId != attempt || row.WakeGeneration != probe.WakeGeneration || row.Phase is "pending" or "terminal_pending" or "completed"))
        {
            var facts = await ReadCapacityFactsAsync(resources, row, attempt.Value, token).ConfigureAwait(false);
            if (facts is { Released: true, Positive: true } && facts.AdmissionToken == probe.Token)
                return BackgroundJobProbeDisposition.AdmissionReleased;
        }
        return Interpret(probe, row);
    }

    private static BackgroundJobProbeDisposition Interpret(BackgroundJobCommitProbe probe, ControlRow? row)
    {
        if (row is null) return BackgroundJobProbeDisposition.Missing;
        if (row.TenantId != probe.TenantId || row.OrganizationId != probe.OrganizationId
            || row.DefinitionKey != probe.DefinitionKey || row.Queue != probe.Queue || row.CapacityGroup != probe.CapacityGroup)
            return BackgroundJobProbeDisposition.ChangedOwner;
        if (probe.Boundary == BackgroundJobCommitBoundary.Renewal)
        {
            var stage = probe.LedgerStage == BackgroundJobLedgerStage.Normal ? "normal"
                : probe.LedgerStage == BackgroundJobLedgerStage.Finalize ? "finalize" : null;
            if (stage is null || probe.OriginalAdmissionToken is null
                || !probe.IsRecovery && probe.OriginalAdmissionToken != probe.Token
                || probe.IsRecovery && (probe.Profile != "external" || stage != "normal"
                    || probe.FinalizeOrdinal != 0 || probe.SelectedNormalAttemptId is not null
                    || probe.OriginalAdmissionToken == probe.Token)
                || row.Profile != probe.Profile || row.LedgerStage != stage
                || row.WakeGeneration != probe.WakeGeneration || row.AttemptId != probe.AttemptId
                || row.OwnershipToken != probe.Token || row.OwnershipKind != (probe.IsRecovery ? "recovery" : "execution")
                || row.NormalAttempts != probe.Ordinal || row.FinalizeAttempts != probe.FinalizeOrdinal
                || row.SelectedNormalAttemptId != probe.SelectedNormalAttemptId || row.Deadline != probe.Deadline
                || row.LedgerAdmittedAt != probe.AdmittedAt || row.OriginalDueAt != probe.OriginalDueAt
                || row.LedgerToken != probe.OriginalAdmissionToken || !row.MatchingAttempt || !row.Incomplete)
                return BackgroundJobProbeDisposition.ChangedOwner;
            return probe.ExpectedUntil is { } expected && row.OwnershipUntil >= expected
                ? BackgroundJobProbeDisposition.Renewed : BackgroundJobProbeDisposition.RolledBack;
        }
        if (row.ProcessedAt is not null) return BackgroundJobProbeDisposition.BusinessCompleted;
        if (row.DeadLetteredAt is not null) return BackgroundJobProbeDisposition.TerminalCompleted;
        if (probe.Boundary == BackgroundJobCommitBoundary.Control && row.WakeGeneration == probe.ExpectedGeneration
            && row.Phase == probe.ExpectedPhase && row.AttemptId == probe.AttemptId && row.OwnershipToken is null)
            return BackgroundJobProbeDisposition.ControlCommitted;
        if (row.WakeGeneration != probe.WakeGeneration || (probe.AttemptId is { } attempt && row.AttemptId != attempt))
            return BackgroundJobProbeDisposition.ChangedOwner;
        if (probe.Boundary == BackgroundJobCommitBoundary.Admission && row.MatchingAttempt && row.LedgerToken == probe.Token)
            return BackgroundJobProbeDisposition.Admitted;
        if (row.OwnershipToken != probe.Token) return BackgroundJobProbeDisposition.ChangedOwner;
        if (probe.Boundary == BackgroundJobCommitBoundary.TerminalClaim && row.Phase == "terminal_pending")
            return BackgroundJobProbeDisposition.ControlCommitted;
        return BackgroundJobProbeDisposition.RolledBack;
    }

    /// <summary>The finite actual locking queries are also exercised by real planner controls.</summary>
    internal static string SelectionSql(BackgroundJobControlLane lane)
    {
        if (lane is BackgroundJobControlLane.ExternalRecovery or BackgroundJobControlLane.ExternalUnarmed)
            return ExternalWakeSelectionSql(lane == BackgroundJobControlLane.ExternalUnarmed);
        if (lane == BackgroundJobControlLane.ExternalFinalize) return ExternalFinalizeSelectionSql();
        if (lane == BackgroundJobControlLane.Retirement) return RetirementSelectionSql();
        if (lane == BackgroundJobControlLane.Exhaustion)
            return $"""
                SELECT j.id FROM background_jobs j WHERE {Incomplete} AND NOT (id=ANY(@blocked))
                    AND ownership_token IS NULL AND available_after<=statement_timestamp()
                    AND ((profile='transactional' AND phase='pending' AND normal_attempts=6 AND definition_key=ANY(@compatible))
                        OR (profile='external' AND phase='finalize_pending' AND finalize_attempts=6
                            AND EXISTS (SELECT 1 FROM unnest(@externalDefinitions,@externalQueues,@externalGroups,@externalTenantWide)
                                AS definition(key,queue,capacity,tenant_wide)
                                WHERE definition.key=j.definition_key AND definition.queue=j.queue
                                    AND definition.capacity=j.capacity_group AND definition.tenant_wide=(j.organization_id IS NULL))))
                ORDER BY available_after,created_at,id LIMIT @batch FOR UPDATE SKIP LOCKED
                """;
        if (lane == BackgroundJobControlLane.Recovery)
            return $"""
                SELECT id FROM background_jobs WHERE {Incomplete} AND NOT (id=ANY(@blocked))
                    AND ownership_token IS NOT NULL AND phase<>'quarantined' AND ownership_until<=statement_timestamp()
                    AND ((ownership_kind='routing' AND phase IN ('pending','finalize_pending') AND profile IN ('transactional','external'))
                        OR (profile='transactional' AND definition_key=ANY(@compatible)))
                ORDER BY ownership_until,created_at,id LIMIT @batch FOR UPDATE SKIP LOCKED
                """;
        var selection = lane switch
        {
            BackgroundJobControlLane.CapacityProbe => "phase = 'reconciling' AND ownership_token IS NULL AND capacity_group IS NOT NULL AND probe_after <= statement_timestamp() AND definition_key = ANY(@compatible) ORDER BY probe_after, created_at, id",
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };
        return $"SELECT id FROM background_jobs WHERE {Incomplete} AND profile = 'transactional' AND NOT (id = ANY(@blocked)) AND {selection} LIMIT @batch FOR UPDATE SKIP LOCKED";
    }

    private async Task<BackgroundJobControlChange?> RecoverAsync(BackgroundJobControlResources resources,
        ControlRow row, BackgroundJobTerminalReason? reason, string errorCode, CancellationToken token)
    {
        if (!row.MatchingAttempt || row.NormalAttempts is < 1 or > 6) return null;
        if (row.CapacityGroup is not null) return await RecoverCapacityRowAsync(resources, row, reason, errorCode, token).ConfigureAwait(false);
        var recoveryToken = ids.NewUuidV4();
        var command = Command(resources, """
            UPDATE background_jobs SET phase = 'reconciling', ownership_token = @recovery,
                ownership_kind = 'recovery', ownership_until = clock_timestamp() + @lease
            WHERE id = @id AND current_attempt_id = @attempt AND ownership_token = @token AND wake_generation = @generation
                AND phase IN ('executing', 'reconciling') AND ownership_kind IN ('execution', 'recovery')
                AND ownership_until <= clock_timestamp() AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            """);
        AddRowFence(command, row);
        command.Parameters.AddWithValue("recovery", recoveryToken);
        command.Parameters.AddWithValue("lease", options.LeaseDuration);
        var acquired = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        if (!acquired) return null;
        var recovery = row with { Phase = "reconciling", OwnershipToken = recoveryToken, OwnershipKind = "recovery" };
        if (reason is not null || row.NormalAttempts == 6)
            return await RequestTerminalAsync(resources, recovery, reason ?? BackgroundJobTerminalReason.RetryExhausted,
                " AND phase = 'reconciling' AND ownership_kind = 'recovery' AND ownership_until > clock_timestamp()", token).ConfigureAwait(false);

        command = Command(resources, """
            UPDATE background_jobs SET phase = 'pending', available_after = clock_timestamp() + @delay,
                last_error = @error, probe_after = NULL,
                ownership_token = NULL, ownership_kind = NULL, ownership_until = NULL
            WHERE id = @id AND current_attempt_id = @attempt AND ownership_token = @token AND wake_generation = @generation
                AND phase = 'reconciling' AND ownership_kind = 'recovery' AND ownership_until > clock_timestamp()
                AND normal_attempts BETWEEN 1 AND 5 AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING id, wake_generation, phase, terminal_reason
            """);
        AddRowFence(command, recovery);
        command.Parameters.AddWithValue("delay", BackgroundJobFailurePolicy.GetRetryDelay(row.NormalAttempts));
        command.Parameters.AddWithValue("error", errorCode);
        return await ReadChangeAsync(resources, token).ConfigureAwait(false);
    }

    private async Task<BackgroundJobControlChange?> ClearExpiredRoutingAsync(BackgroundJobControlResources resources,
        ControlRow row, CancellationToken token)
    {
        var command = Command(resources, """
            UPDATE background_jobs SET ownership_token = NULL, ownership_kind = NULL, ownership_until = NULL,
                handoff_check_at = clock_timestamp()
            WHERE id = @id AND ownership_token = @token AND wake_generation = @generation AND phase IN ('pending','finalize_pending')
                AND ownership_kind = 'routing' AND ownership_until <= clock_timestamp()
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING id, wake_generation, phase, terminal_reason
            """);
        AddRowFence(command, row);
        return await ReadChangeAsync(resources, token).ConfigureAwait(false);
    }

    private async Task<BackgroundJobControlChange?> RequestTerminalAsync(BackgroundJobControlResources resources,
        ControlRow row, BackgroundJobTerminalReason reason, string authority, CancellationToken token)
    {
        var command = Command(resources, $"""
            UPDATE background_jobs SET phase = 'terminal_pending', terminal_reason = @reason, available_after = clock_timestamp(),
                ownership_token = NULL, ownership_kind = NULL, ownership_until = NULL
            WHERE id = @id AND ownership_token IS NOT DISTINCT FROM @token AND wake_generation = @generation
                AND current_attempt_id IS NOT DISTINCT FROM @attempt AND {Incomplete}{authority}
            RETURNING id, wake_generation, phase, terminal_reason
            """);
        AddRowFence(command, row);
        command.Parameters.AddWithValue("reason", BackgroundJobFailurePolicy.TerminalCode(reason));
        return await ReadChangeAsync(resources, token).ConfigureAwait(false);
    }

    private static async Task<BackgroundJobControlChange?> ReadChangeAsync(BackgroundJobControlResources resources, CancellationToken token)
    {
        resources.SetReader(await resources.Command!.ExecuteReaderAsync(token).ConfigureAwait(false));
        BackgroundJobControlChange? value = await resources.Reader!.ReadAsync(token).ConfigureAwait(false)
            ? new(resources.Reader.GetGuid(0), resources.Reader.GetInt64(1), resources.Reader.GetString(2),
                resources.Reader.IsDBNull(3) ? null : resources.Reader.GetString(3)) : null;
        await FinishAsync(resources).ConfigureAwait(false);
        return value;
    }

    private static BackgroundJobCommitProbe Probe(ControlRow row, BackgroundJobControlChange change) =>
        new(BackgroundJobCommitBoundary.Control, row.JobId, row.TenantId, row.OrganizationId, row.DefinitionKey,
            row.Queue, row.WakeGeneration, row.AttemptId, row.OwnershipToken ?? Guid.Empty, change.WakeGeneration, change.Phase,
            CapacityGroup: row.CapacityGroup);

    private async Task<bool> OpenAsync(BackgroundJobControlResources resources, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        options.Validate();
        if (runtimeGate?.TryAdmit() == false) return false;
        resources.Connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        resources.Transaction = await resources.Connection.BeginTransactionAsync(token).ConfigureAwait(false);
        return true;
    }

    private NpgsqlCommand Command(BackgroundJobControlResources resources, string sql)
    {
        var command = new NpgsqlCommand(sql, resources.Connection!, resources.Transaction)
        { CommandTimeout = (int)Math.Ceiling(options.SqlTimeout.TotalSeconds) };
        resources.SetCommand(command);
        return command;
    }

    private static void AddRowFence(NpgsqlCommand command, ControlRow row)
    {
        command.Parameters.AddWithValue("id", row.JobId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, (object?)row.AttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, (object?)row.OwnershipToken ?? DBNull.Value);
        command.Parameters.AddWithValue("generation", row.WakeGeneration);
    }

    private CancellationTokenSource Deadline(CancellationToken cancellationToken, bool probe = false)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(probe ? options.ProbeTimeout : options.SqlTimeout);
        return deadline;
    }

    private static async Task CommitAsync(BackgroundJobControlResources resources, CancellationToken token)
    {
        resources.CommitAttempted = true;
        await resources.Transaction!.CommitAsync(token).ConfigureAwait(false);
        resources.CommitAcknowledged = true;
    }

    private static async Task FinishAsync(BackgroundJobControlResources resources)
    {
        await resources.FinishReaderAsync().ConfigureAwait(false);
        await resources.FinishCommandAsync().ConfigureAwait(false);
    }

    private async Task<ControlRow?> LockRowAsync(BackgroundJobControlResources resources, Guid id, CancellationToken token)
    {
        var command = Command(resources, """
            SELECT j.id, j.tenant_id, j.organization_id, j.definition_key, j.queue, j.phase, j.normal_attempts,
                j.current_attempt_id, j.ownership_token, j.ownership_kind, j.ownership_until, j.attempt_deadline,
                j.wake_generation, j.terminal_reason, j.processed_at, j.dead_lettered_at, j.profile, j.capacity_group, j.original_due_at,
                j.finalize_attempts,j.final_outcome_attempt_id,j.probe_after
            FROM background_jobs j WHERE j.id = @id FOR UPDATE
            """);
        command.Parameters.AddWithValue("id", id);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        ControlRow? row = null;
        if (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
        {
            var r = resources.Reader;
            row = new(r.GetGuid(0), r.GetGuid(1), GuidOrNull(r, 2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6),
                GuidOrNull(r, 7), GuidOrNull(r, 8), r.IsDBNull(9) ? null : r.GetString(9), TimeOrNull(r, 10), TimeOrNull(r, 11),
                r.GetInt64(12), r.IsDBNull(13) ? null : r.GetString(13), TimeOrNull(r, 14), TimeOrNull(r, 15),
                null, null, null, null, r.GetString(16), r.IsDBNull(17) ? null : r.GetString(17), r.GetFieldValue<DateTimeOffset>(18))
            { FinalizeAttempts = r.GetInt32(19), SelectedNormalAttemptId = GuidOrNull(r, 20), ProbeAfter = TimeOrNull(r, 21) };
        }
        await FinishAsync(resources).ConfigureAwait(false);
        if (row?.AttemptId is not { } attemptId) return row;

        // A SELECT that waited for the job lock can refresh that row while a
        // joined attempt still comes from its older statement snapshot. Read
        // the immutable ledger in a fresh statement after acquiring the lock.
        command = Command(resources, """
            SELECT admission_token, stage, ordinal, deadline, admitted_at,final_outcome_attempt_id FROM background_job_attempts
            WHERE id = @attempt AND job_id = @id AND tenant_id = @tenant
                AND organization_id IS NOT DISTINCT FROM @organization
            """);
        command.Parameters.AddWithValue("attempt", attemptId);
        command.Parameters.AddWithValue("id", row.JobId);
        command.Parameters.AddWithValue("tenant", row.TenantId);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)row.OrganizationId ?? DBNull.Value);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        if (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
        {
            var r = resources.Reader;
            row = row with
            {
                LedgerToken = r.GetGuid(0),
                LedgerStage = r.GetString(1),
                LedgerOrdinal = r.GetInt32(2),
                LedgerDeadline = r.GetFieldValue<DateTimeOffset>(3),
                LedgerAdmittedAt = r.GetFieldValue<DateTimeOffset>(4),
                LedgerSelectedNormalAttemptId = GuidOrNull(r, 5)
            };
        }
        await FinishAsync(resources).ConfigureAwait(false);
        return row;
    }

    private static Guid? GuidOrNull(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    private static DateTimeOffset? TimeOrNull(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private sealed record ControlRow(Guid JobId, Guid TenantId, Guid? OrganizationId, string DefinitionKey, string Queue,
        string Phase, int NormalAttempts, Guid? AttemptId, Guid? OwnershipToken, string? OwnershipKind,
        DateTimeOffset? OwnershipUntil, DateTimeOffset? Deadline, long WakeGeneration, string? TerminalReason,
        DateTimeOffset? ProcessedAt, DateTimeOffset? DeadLetteredAt, Guid? LedgerToken, string? LedgerStage,
        int? LedgerOrdinal, DateTimeOffset? LedgerDeadline, string Profile, string? CapacityGroup, DateTimeOffset OriginalDueAt)
    {
        internal DateTimeOffset? LedgerAdmittedAt { get; init; }
        internal DateTimeOffset? ProbeAfter { get; init; }
        internal int FinalizeAttempts { get; init; }
        internal Guid? SelectedNormalAttemptId { get; init; }
        internal Guid? LedgerSelectedNormalAttemptId { get; init; }
        internal bool Incomplete => ProcessedAt is null && DeadLetteredAt is null && TerminalReason is null;
        internal bool MatchingNormalAttempt => AttemptId is not null && LedgerToken is not null && LedgerStage == "normal"
            && LedgerOrdinal == NormalAttempts && LedgerDeadline == Deadline && LedgerSelectedNormalAttemptId is null;
        internal bool MatchingAttempt => MatchingNormalAttempt || Profile == "external" && AttemptId is not null
            && LedgerToken is not null && LedgerStage == "finalize" && LedgerOrdinal == FinalizeAttempts
            && FinalizeAttempts is >= 1 and <= 6 && LedgerDeadline == Deadline
            && SelectedNormalAttemptId is not null && SelectedNormalAttemptId != AttemptId
            && LedgerSelectedNormalAttemptId == SelectedNormalAttemptId;
        internal bool Matches(BackgroundJobExecutionIdentity identity) => JobId == identity.JobId.Value && TenantId == identity.TenantId.Value
            && OrganizationId == identity.OrganizationId?.Value && DefinitionKey == identity.DefinitionKey && Queue == identity.Queue
            && AttemptId == identity.AttemptId.Value && OwnershipToken == identity.Token && WakeGeneration == identity.WakeGeneration
            && Deadline == identity.Deadline && NormalAttempts == identity.NormalAttemptOrdinal
            && CapacityGroup == identity.CapacityGroup && Profile == identity.Profile && OwnershipKind == "execution"
            && FinalizeAttempts == identity.FinalizeAttemptOrdinal && SelectedNormalAttemptId == identity.SelectedNormalAttemptId?.Value
            && LedgerStage == (identity.LedgerStage == BackgroundJobLedgerStage.Normal ? "normal" : "finalize")
            && LedgerAdmittedAt == identity.AdmittedAt && OriginalDueAt == identity.OriginalDueAt && MatchingAttempt;
        internal bool Matches(BackgroundJobRoutingClaim claim) => JobId == claim.JobId.Value && TenantId == claim.TenantId.Value
            && OrganizationId == claim.OrganizationId?.Value && DefinitionKey == claim.DefinitionKey && Queue == claim.Queue
            && OwnershipToken == claim.Token && WakeGeneration == claim.WakeGeneration;
    }
}

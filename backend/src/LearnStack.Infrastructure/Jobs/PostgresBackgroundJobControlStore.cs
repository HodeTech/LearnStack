using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Time;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Payload-free short transactional control; no operation grants execution authority.</summary>
internal sealed class PostgresBackgroundJobControlStore(PrivilegedDataSource source,
    BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeOptions options, IGuidFactory ids,
    BackgroundJobRuntimeGate? runtimeGate = null)
{
    private const string Incomplete = "processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL";

    internal async Task<OwnedBackgroundJobControl<IReadOnlyList<BackgroundJobRenewal>>> RenewBatchAsync(
        IReadOnlyList<BackgroundJobExecutionIdentity> identities, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(identities);
        var frozen = identities.ToArray();
        if (frozen.Length > 64 || frozen.Any(identity => identity is null)
            || frozen.Select(identity => identity.JobId.Value).Distinct().Count() != frozen.Length)
            throw new ArgumentException("Renewal requires at most 64 distinct acknowledged invocations.", nameof(identities));
        options.Validate();
        var resources = new BackgroundJobControlResources();
        var probes = new List<BackgroundJobCommitProbe>();
        IReadOnlyList<BackgroundJobRenewal>? value = null;
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
                        @attempts, @tokens, @generations, @deadlines, @ordinals)
                        AS x(id, tenant, organization, definition, queue, attempt, token, generation, deadline, ordinal))
                    UPDATE background_jobs j SET ownership_until = LEAST(renewal_clock.at + @lease, j.attempt_deadline)
                    FROM input x, renewal_clock
                    WHERE j.id = x.id AND j.id = ANY(@locked) AND j.tenant_id = x.tenant
                        AND j.organization_id IS NOT DISTINCT FROM x.organization
                        AND j.definition_key = x.definition AND j.queue = x.queue
                        AND j.profile = 'transactional' AND j.capacity_group IS NULL
                        AND j.current_attempt_id = x.attempt AND j.ownership_token = x.token
                        AND j.wake_generation = x.generation AND j.normal_attempts = x.ordinal
                        AND j.attempt_deadline = x.deadline AND j.phase = 'executing' AND j.ownership_kind = 'execution'
                        AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL AND j.terminal_reason IS NULL
                        AND j.ownership_until > renewal_clock.at AND j.attempt_deadline > renewal_clock.at
                    RETURNING j.id, j.ownership_until, renewal_clock.at
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
                command.Parameters.AddWithValue("locked", locked.ToArray());
                command.Parameters.AddWithValue("lease", options.LeaseDuration);
                var renewals = new List<BackgroundJobRenewal>();
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                while (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    var identity = frozen.Single(item => item.JobId.Value == resources.Reader.GetGuid(0));
                    renewals.Add(new(identity, resources.Reader.GetFieldValue<DateTimeOffset>(1), resources.Reader.GetFieldValue<DateTimeOffset>(2)));
                }
                await FinishAsync(resources).ConfigureAwait(false);
                probes.AddRange(frozen.Select(identity => BackgroundJobCommitProbe.ForBusiness(identity) with
                {
                    Boundary = BackgroundJobCommitBoundary.Renewal,
                    ExpectedUntil = renewals.SingleOrDefault(renewal => ReferenceEquals(renewal.Identity, identity))?.OwnershipUntil,
                }));
                await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                value = renewals.AsReadOnly();
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, probes.AsReadOnly());
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
                if (row is not null && row.Matches(identity) && row.Phase == "executing" && row.OwnershipKind == "execution"
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
                if (row is not null && row.Matches(claim) && row.Phase == "pending" && row.OwnershipKind == "routing" && row.Incomplete)
                {
                    value = await RequestTerminalAsync(resources, row, BackgroundJobTerminalReason.InvalidEnvelope,
                        " AND ownership_kind = 'routing' AND ownership_until > clock_timestamp()", deadline.Token).ConfigureAwait(false);
                    if (value is not null) probes.Add(Probe(row, value));
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
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
                    && definition.Profile == BackgroundJobExecutionProfile.Transactional && definition.CapacityGroup is null).ToArray();
                var compatible = active.Concat(definitions.RetiredKeys).ToArray();
                var command = Command(resources, SelectionSql(lane));
                command.Parameters.AddWithValue("blocked", retainedJobIds.ToArray());
                command.Parameters.AddWithValue("compatible", compatible);
                command.Parameters.AddWithValue("retired", definitions.RetiredKeys.ToArray());
                command.Parameters.AddWithValue("batch", options.ControlBatchSize);
                var selected = new List<Guid>();
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                while (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false)) selected.Add(resources.Reader.GetGuid(0));
                await FinishAsync(resources).ConfigureAwait(false);
                var changes = new List<BackgroundJobControlChange>();
                foreach (var id in selected)
                {
                    var row = await LockRowAsync(resources, id, deadline.Token).ConfigureAwait(false);
                    if (row is null || !row.Incomplete) continue;
                    var resolution = definitions.Resolve(row.DefinitionKey);
                    if (resolution is { Disposition: BackgroundJobDefinitionDisposition.Active, Definition: { } definition }
                        && (definition.Profile != BackgroundJobExecutionProfile.Transactional || definition.CapacityGroup is not null
                            || definition.Queue != row.Queue
                            || (definition.Scope == BackgroundJobScope.TenantWide ? row.OrganizationId is not null : row.OrganizationId is null)))
                        continue;
                    BackgroundJobControlChange? changed = null;
                    if (lane == BackgroundJobControlLane.Recovery && row.Phase == "pending" && row.OwnershipKind == "routing")
                        changed = await ClearExpiredRoutingAsync(resources, row, deadline.Token).ConfigureAwait(false);
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
                var disposition = Interpret(probe, row);
                value = new(disposition, probe.JobId, row?.Phase, row?.WakeGeneration ?? 0, row?.NormalAttempts ?? 0,
                    row?.AttemptId, row?.OwnershipToken, row?.OwnershipUntil, row?.Deadline, dbNow);
                // A read probe needs no COMMIT and cannot create recursive COMMIT uncertainty.
                await resources.Transaction!.RollbackAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, Array.Empty<BackgroundJobCommitProbe>());
    }

    private static BackgroundJobProbeDisposition Interpret(BackgroundJobCommitProbe probe, ControlRow? row)
    {
        if (row is null) return BackgroundJobProbeDisposition.Missing;
        if (row.TenantId != probe.TenantId || row.OrganizationId != probe.OrganizationId
            || row.DefinitionKey != probe.DefinitionKey || row.Queue != probe.Queue)
            return BackgroundJobProbeDisposition.ChangedOwner;
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
        if (probe.Boundary == BackgroundJobCommitBoundary.Renewal && probe.ExpectedUntil is { } expected
            && row.OwnershipUntil >= expected && row.MatchingAttempt)
            return BackgroundJobProbeDisposition.Renewed;
        return BackgroundJobProbeDisposition.RolledBack;
    }

    /// <summary>The finite actual locking queries are also exercised by real planner controls.</summary>
    internal static string SelectionSql(BackgroundJobControlLane lane)
    {
        var selection = lane switch
        {
            BackgroundJobControlLane.Recovery => "ownership_token IS NOT NULL AND phase <> 'quarantined' AND ownership_until <= statement_timestamp() AND (ownership_kind = 'routing' OR definition_key = ANY(@compatible)) ORDER BY ownership_until, created_at, id",
            BackgroundJobControlLane.Exhaustion => "ownership_token IS NULL AND phase = 'pending' AND normal_attempts = 6 AND available_after <= statement_timestamp() AND definition_key = ANY(@compatible) ORDER BY available_after, created_at, id",
            BackgroundJobControlLane.Retirement => "ownership_token IS NULL AND phase = 'pending' AND normal_attempts < 6 AND available_after <= statement_timestamp() AND definition_key = ANY(@retired) ORDER BY available_after, created_at, id",
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };
        return $"SELECT id FROM background_jobs WHERE {Incomplete} AND profile = 'transactional' AND capacity_group IS NULL AND NOT (id = ANY(@blocked)) AND {selection} LIMIT @batch FOR UPDATE SKIP LOCKED";
    }

    private async Task<BackgroundJobControlChange?> RecoverAsync(BackgroundJobControlResources resources,
        ControlRow row, BackgroundJobTerminalReason? reason, string errorCode, CancellationToken token)
    {
        if (!row.MatchingAttempt || row.NormalAttempts is < 1 or > 6) return null;
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
            WHERE id = @id AND ownership_token = @token AND wake_generation = @generation AND phase = 'pending'
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
            row.Queue, row.WakeGeneration, row.AttemptId, row.OwnershipToken ?? Guid.Empty, change.WakeGeneration, change.Phase);

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
                j.wake_generation, j.terminal_reason, j.processed_at, j.dead_lettered_at
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
                null, null, null, null);
        }
        await FinishAsync(resources).ConfigureAwait(false);
        if (row?.AttemptId is not { } attemptId) return row;

        // A SELECT that waited for the job lock can refresh that row while a
        // joined attempt still comes from its older statement snapshot. Read
        // the immutable ledger in a fresh statement after acquiring the lock.
        command = Command(resources, """
            SELECT admission_token, stage, ordinal, deadline FROM background_job_attempts
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
                LedgerDeadline = r.GetFieldValue<DateTimeOffset>(3)
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
        int? LedgerOrdinal, DateTimeOffset? LedgerDeadline)
    {
        internal bool Incomplete => ProcessedAt is null && DeadLetteredAt is null && TerminalReason is null;
        internal bool MatchingAttempt => AttemptId is not null && LedgerToken is not null && LedgerStage == "normal"
            && LedgerOrdinal == NormalAttempts && LedgerDeadline == Deadline;
        internal bool Matches(BackgroundJobExecutionIdentity identity) => JobId == identity.JobId.Value && TenantId == identity.TenantId.Value
            && OrganizationId == identity.OrganizationId?.Value && DefinitionKey == identity.DefinitionKey && Queue == identity.Queue
            && AttemptId == identity.AttemptId.Value && OwnershipToken == identity.Token && WakeGeneration == identity.WakeGeneration
            && Deadline == identity.Deadline && NormalAttempts == identity.NormalAttemptOrdinal;
        internal bool Matches(BackgroundJobRoutingClaim claim) => JobId == claim.JobId.Value && TenantId == claim.TenantId.Value
            && OrganizationId == claim.OrganizationId?.Value && DefinitionKey == claim.DefinitionKey && Queue == claim.Queue
            && OwnershipToken == claim.Token && WakeGeneration == claim.WakeGeneration;
    }
}

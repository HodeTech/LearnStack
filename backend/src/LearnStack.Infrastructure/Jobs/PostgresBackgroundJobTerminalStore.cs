using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>The single bypass job terminalizer; state and sixth MUST write share its actual transaction.</summary>
internal sealed class PostgresBackgroundJobTerminalStore(PrivilegedDataSource source,
    IServiceScopeFactory scopes, BackgroundJobRuntimeOptions options, IGuidFactory ids,
    BackgroundJobRuntimeGate? runtimeGate = null) : JobTerminalTransition.MintingSource
{
    internal const string ClaimSelectionSql = """
        SELECT id FROM background_jobs
        WHERE phase = 'terminal_pending' AND terminal_reason IS NOT NULL
            AND processed_at IS NULL AND dead_lettered_at IS NULL
            AND profile = 'transactional'
            AND NOT EXISTS (SELECT 1 FROM background_job_reservations r
                WHERE r.tenant_id=background_jobs.tenant_id AND r.job_id=background_jobs.id AND r.released_at IS NULL)
            AND available_after <= statement_timestamp()
            AND (ownership_token IS NULL OR ownership_until <= statement_timestamp())
            AND NOT (id = ANY(@blocked))
        ORDER BY available_after, created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
        """;
    private const string ExactClaim = """
        id = @id AND tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization
        AND definition_key = @definition AND queue = @queue AND profile = 'transactional'
        AND capacity_group IS NOT DISTINCT FROM @capacity
        AND NOT EXISTS (SELECT 1 FROM background_job_reservations r
            WHERE r.tenant_id=background_jobs.tenant_id AND r.job_id=background_jobs.id AND r.released_at IS NULL)
        AND wake_generation = @generation AND current_attempt_id IS NOT DISTINCT FROM @attempt
        AND normal_attempts = @attempts AND ownership_token = @token AND ownership_kind = 'recovery'
        AND phase = 'terminal_pending' AND terminal_reason = @reason AND processed_at IS NULL AND dead_lettered_at IS NULL
        """;

    internal async Task<OwnedBackgroundJobControl<BackgroundJobTerminalClaim>> TryClaimTerminalAsync(
        IReadOnlyCollection<Guid> retainedJobIds, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(retainedJobIds);
        if (retainedJobIds.Count > BackgroundJobControlLimits.MaximumRetainedIds) throw new ArgumentException("Terminal selection requires a bounded retained inventory.", nameof(retainedJobIds));
        var resources = new BackgroundJobControlResources();
        BackgroundJobTerminalClaim? value = null;
        var probes = new List<BackgroundJobCommitProbe>();
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var command = Command(resources, ClaimSelectionSql);
                command.Parameters.AddWithValue("blocked", retainedJobIds.ToArray());
                var id = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false) as Guid?;
                await resources.FinishCommandAsync().ConfigureAwait(false);
                if (id is { } job)
                {
                    command = Command(resources, """
                        UPDATE background_jobs SET ownership_token = @token, ownership_kind = 'recovery',
                            ownership_until = clock_timestamp() + @lease
                        WHERE id = @id AND phase = 'terminal_pending' AND terminal_reason IS NOT NULL
                            AND processed_at IS NULL AND dead_lettered_at IS NULL
                            AND profile = 'transactional'
                            AND NOT EXISTS (SELECT 1 FROM background_job_reservations r
                                WHERE r.tenant_id=background_jobs.tenant_id AND r.job_id=background_jobs.id AND r.released_at IS NULL)
                            AND available_after <= clock_timestamp()
                            AND (ownership_token IS NULL OR ownership_until <= clock_timestamp())
                        RETURNING id, tenant_id, organization_id, definition_key, queue, wake_generation,
                            current_attempt_id, normal_attempts, terminal_reason, ownership_token, ownership_until, clock_timestamp(), capacity_group
                        """);
                    command.Parameters.AddWithValue("id", job);
                    command.Parameters.AddWithValue("token", ids.NewUuidV4());
                    command.Parameters.AddWithValue("lease", options.LeaseDuration);
                    resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                    var candidate = await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false)
                        ? BackgroundJobTerminalClaim.FromReturnedRow(resources.Reader) : null;
                    await FinishAsync(resources).ConfigureAwait(false);
                    if (candidate is not null) probes.Add(candidate.Probe(BackgroundJobCommitBoundary.TerminalClaim));
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                    value = candidate;
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, probes.AsReadOnly());
    }

    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> TryTerminalizeAsync(
        BackgroundJobTerminalClaim claim, string validatedAttemptTraceparent, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(claim);
        var resources = new BackgroundJobControlResources();
        BackgroundJobControlChange? value = null;
        var probes = new[] { claim.Probe(BackgroundJobCommitBoundary.Terminal) };
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false)
                && await LockClaimAsync(resources, claim, deadline.Token).ConfigureAwait(false))
            {
                var command = Command(resources, $"""
                    UPDATE background_jobs SET dead_lettered_at = clock_timestamp()
                    WHERE {ExactClaim} AND ownership_until > clock_timestamp()
                    RETURNING id, tenant_id, organization_id, definition_key, queue, wake_generation,
                        current_attempt_id, normal_attempts, terminal_reason, dead_lettered_at,
                        old.ownership_token AS acquisition_token
                    """);
                AddClaim(command, claim);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                JobTerminalTransition? witness = null;
                if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                    witness = FromBypassUpdate(resources.Connection!, resources.Transaction!, resources.Reader, validatedAttemptTraceparent);
                await FinishAsync(resources).ConfigureAwait(false);
                if (witness is not null)
                {
                    resources.AuditScope = scopes.CreateAsyncScope();
                    var audit = resources.AuditScope.Value.ServiceProvider.GetRequiredService<IAuditStore>();
                    var draft = new AuditEntryDraft
                    {
                        Id = AuditEntryId.From(ids.NewUuidV7()),
                        TenantId = TenantId.From(witness.TenantId),
                        OrganizationId = witness.OrganizationId is { } organization ? OrganizationId.From(organization) : null,
                        ActorUserId = UserId.SystemActor,
                        ActorEmail = null,
                        ModuleName = "platform",
                        Operation = "platform.job.dead_letter",
                        OperationType = OperationType.SecurityEvent,
                        OperationClass = OperationClass.Must,
                        EntityType = null,
                        EntityId = null,
                        Outcome = AuditOutcome.Success,
                        ErrorKey = null,
                        Reason = null,
                        BeforeState = null,
                        AfterState = null,
                        Changes = null,
                        CorrelationId = witness.CorrelationId,
                        IpAddress = null,
                        UserAgent = null,
                        Timestamp = witness.DeadLetteredAt,
                        Metadata = witness.Metadata,
                    };
                    await audit.WriteJobTerminalAsync(draft, witness, resources.Connection!, resources.Transaction!, deadline.Token).ConfigureAwait(false);
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                    value = new(claim.JobId, claim.WakeGeneration, "terminal_pending", claim.Reason);
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, probes);
    }

    internal async Task<OwnedBackgroundJobControl<BackgroundJobControlChange>> BackoffKnownAuditRollbackAsync(
        BackgroundJobTerminalClaim claim, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(claim);
        var resources = new BackgroundJobControlResources();
        BackgroundJobControlChange? value = null;
        var probes = new[] { claim.Probe(BackgroundJobCommitBoundary.Control) with
            { ExpectedGeneration = claim.WakeGeneration, ExpectedPhase = "terminal_pending" } };
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false)
                && await LockClaimAsync(resources, claim, deadline.Token).ConfigureAwait(false))
            {
                // The exact old claim may have expired during known rollback.
                // Equality refuses a newer lease/owner; expiry never proves rollback.
                var command = Command(resources, $"""
                    UPDATE background_jobs SET available_after = clock_timestamp() + @backoff,
                        last_error = 'audit_unavailable', ownership_token = NULL, ownership_kind = NULL, ownership_until = NULL
                    WHERE {ExactClaim} AND ownership_until = @until
                    RETURNING id, wake_generation, phase, terminal_reason
                    """);
                AddClaim(command, claim);
                command.Parameters.AddWithValue("until", claim.OwnershipUntil);
                command.Parameters.AddWithValue("backoff", options.RecoveryProbeInterval);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                var changed = await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false);
                await FinishAsync(resources).ConfigureAwait(false);
                await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                if (changed) value = new(claim.JobId, claim.WakeGeneration, "terminal_pending", claim.Reason);
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, probes);
    }

    private async Task<bool> LockClaimAsync(BackgroundJobControlResources resources, BackgroundJobTerminalClaim claim, CancellationToken token)
    {
        // No time predicate before a potentially blocking row lock.
        var command = Command(resources, $"SELECT id FROM background_jobs WHERE {ExactClaim} FOR UPDATE");
        AddClaim(command, claim);
        var matched = await command.ExecuteScalarAsync(token).ConfigureAwait(false) is Guid;
        await resources.FinishCommandAsync().ConfigureAwait(false);
        return matched;
    }

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

    private static void AddClaim(NpgsqlCommand command, BackgroundJobTerminalClaim claim)
    {
        command.Parameters.AddWithValue("id", claim.JobId);
        command.Parameters.AddWithValue("tenant", claim.TenantId);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)claim.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("definition", claim.DefinitionKey);
        command.Parameters.AddWithValue("queue", claim.Queue);
        command.Parameters.AddWithValue("capacity", NpgsqlDbType.Text, (object?)claim.CapacityGroup ?? DBNull.Value);
        command.Parameters.AddWithValue("generation", claim.WakeGeneration);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, (object?)claim.AttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue("attempts", claim.NormalAttempts);
        command.Parameters.AddWithValue("token", claim.Token);
        command.Parameters.AddWithValue("reason", claim.Reason);
    }

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.SqlTimeout);
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
}

using System.Data.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>The operator's narrow producer reset of one retry-exhausted outbox row (ADR-0103 § 3).</summary>
public sealed partial class OutboxDispatchStore
{
    /// <summary>
    /// Resets one dead-lettered outbox row whose terminal reason is <c>retry_exhausted</c>, under the fence
    /// <c>(attempts, dead_lettered_at)</c>, renewing its attempt budget, and writes the MUST result row on the same
    /// transaction. Every other reason and every processed row is refused.
    /// </summary>
    /// <remarks>
    /// A publication that already reached a subscriber is absorbed by stable event-id acceptance, which never reopens
    /// an existing delivery row, so the reset reaches only subscriptions that have no delivery row yet. A row whose
    /// stored type is now retired or unknown would only be dead-lettered again, so it is refused.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The admission is not for a reset, is stale or was spent.</exception>
    /// <exception cref="OperatorRecoveryUnavailableException">The lane or the database was unavailable, or the commit's outcome is unknown.</exception>
    /// <exception cref="AuditWriteFailedException">The MUST row could not be written; nothing changed.</exception>
    public async Task<OperatorRecoveryResult> ResetAsync(
        OutboxResetRequest request, PlatformAdmission admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admission);
        OperatorRecoveryAdmission.Consume(admission, PlatformOperations.DeliveryReset, _clock);
        if (request.TenantId == Guid.Empty || request.TenantId == TenantId.PlatformSentinel.Value || request.EventId == Guid.Empty)
        {
            return new OperatorRecoveryResult(OperatorRecoveryOutcome.NotFound);
        }

        if (request.Attempts < 0)
        {
            return new OperatorRecoveryResult(OperatorRecoveryOutcome.StaleFence);
        }

        var progress = new RecoveryProgress();
        try
        {
            return await ResetCoreAsync(request, admission, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (progress.CommitAttempted
            && (failure is AggregateException || OperatorRecoveryFaults.IsUnavailable(failure, cancellationToken)))
        {
            // The COMMIT was sent, so whatever failed afterwards (the connection's cleanup included) leaves its
            // outcome unknown, and the operator inspects the row before repeating.
            throw new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true, failure);
        }
        catch (Exception failure) when (OperatorRecoveryFaults.IsUnavailable(failure, cancellationToken))
        {
            throw new OperatorRecoveryUnavailableException(cause: failure);
        }
    }

    private async Task<OperatorRecoveryResult> ResetCoreAsync(
        OutboxResetRequest request, PlatformAdmission admission,
        RecoveryProgress progress, CancellationToken cancellationToken)
    {
        using var deadline = Deadline(cancellationToken);
        await using var ownedConnection = await OpenConnectionAsync(deadline.Token, operatorLane: true).ConfigureAwait(false);
        var connection = ownedConnection.Connection;
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);

        string type;
        bool processed;
        bool leased;
        DateTimeOffset? deadLettered;
        string? reason;
        int attempts;
        await using (var rowLock = new NpgsqlCommand("""
            SELECT type, processed_at IS NOT NULL, dead_lettered_at, terminal_reason, attempts,
                locked_by IS NOT NULL OR locked_until IS NOT NULL
            FROM outbox_messages WHERE id = @id AND tenant_id = @tenant FOR UPDATE
            """, connection, transaction))
        {
            Configure(rowLock);
            rowLock.Parameters.AddWithValue("id", request.EventId);
            rowLock.Parameters.AddWithValue("tenant", request.TenantId);
            await using var reader = await rowLock.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                return new OperatorRecoveryResult(OperatorRecoveryOutcome.NotFound);
            }

            type = reader.GetString(0);
            processed = reader.GetBoolean(1);
            deadLettered = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
            reason = reader.IsDBNull(3) ? null : reader.GetString(3);
            attempts = reader.GetInt32(4);
            leased = reader.GetBoolean(5);
        }

        if (deadLettered is not { } deadLetteredAt || deadLetteredAt.UtcTicks != request.DeadLetteredAt.UtcTicks
            || attempts != request.Attempts)
        {
            return new OperatorRecoveryResult(OperatorRecoveryOutcome.StaleFence);
        }

        var disposition = _registry.Resolve(type).Disposition;
        var refusal = processed ? "processed"
            : leased ? "leased"
            : reason != "retry_exhausted" ? "reason_refused"
            : disposition is OutboxEventDisposition.Retired or OutboxEventDisposition.Unknown ? "type_not_dispatchable"
            : !await TenantServesAsync(connection, transaction, request.TenantId, deadline.Token).ConfigureAwait(false)
                ? "tenant_not_serving"
                : null;
        if (refusal is not null)
        {
            return new OperatorRecoveryResult(OperatorRecoveryOutcome.NotRecoverable, Refusal: refusal);
        }

        OperatorRecoveryTransition witness;
        await using (var reset = new NpgsqlCommand("""
            UPDATE outbox_messages SET terminal_reason = NULL, dead_lettered_at = NULL, attempts = 0, last_error = NULL,
                locked_by = NULL, locked_until = NULL, available_after = clock_timestamp()
            WHERE id = @id AND tenant_id = @tenant AND processed_at IS NULL AND dead_lettered_at = @dead
                AND terminal_reason = 'retry_exhausted' AND attempts = @attempts
                AND locked_by IS NULL AND locked_until IS NULL
            RETURNING id, tenant_id, organization_id, old.attempts, old.dead_lettered_at, old.terminal_reason, new.attempts
            """, connection, transaction))
        {
            Configure(reset);
            reset.Parameters.AddWithValue("id", request.EventId);
            reset.Parameters.AddWithValue("tenant", request.TenantId);
            reset.Parameters.AddWithValue("dead", deadLetteredAt);
            reset.Parameters.AddWithValue("attempts", request.Attempts);
            await using var reader = await reset.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                return new OperatorRecoveryResult(OperatorRecoveryOutcome.StaleFence);
            }

            witness = RecoveryMint.Reset(connection, transaction, reader, admission);
        }

        await WriteRecoveryRowAsync(witness, admission, connection, transaction, deadline.Token).ConfigureAwait(false);
        progress.CommitAttempted = true;
        try
        {
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is NpgsqlException or OperationCanceledException or IOException)
        {
            // COMMIT may have reached PostgreSQL; the row and its audit commit together or not at all, so the
            // operator lists again (a landed reset leaves the dead-lettered list) or reads the tenant's audit row by
            // the correlation id.
            ownedConnection.RecordCommitFailure(failure);
            throw new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true, failure);
        }

        return new OperatorRecoveryResult(OperatorRecoveryOutcome.Applied, witness.AfterCount);
    }

    private async Task WriteRecoveryRowAsync(
        OperatorRecoveryTransition witness, PlatformAdmission admission, DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var draft = new AuditEntryDraft
        {
            Id = AuditEntryId.From(_ids.NewUuidV7()),
            TenantId = TenantId.From(witness.TenantId),
            OrganizationId = witness.OrganizationId is { } organization ? OrganizationId.From(organization) : null,
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = "platform",
            Operation = admission.Operation.AuditSlug,
            OperationType = OperationType.SecurityEvent,
            OperationClass = OperationClass.Must,
            EntityType = null,
            EntityId = null,
            Outcome = AuditOutcome.Success,
            ErrorKey = null,
            Reason = admission.Operation.ReasonSlug,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = witness.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = _clock.UtcNow,
            Metadata = witness.Metadata,
        };
        await audit.WriteOperatorRecoveryAsync(draft, witness, connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TenantServesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant, CancellationToken cancellationToken)
    {
        var lifecycle = _lifecycle ?? throw new NotSupportedException("The tenant lifecycle projection is not configured.");
        await using var command = new NpgsqlCommand(
            "SELECT status, deleted_at IS NOT NULL, default_organization_id FROM tenants WHERE id = @tenant", connection, transaction);
        Configure(command);
        command.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            && lifecycle.AllowsTenant(
                reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                BackgroundJobRecipientPolicy.ServingTenants);
    }

    /// <summary>Whether the COMMIT was sent, so a later fault is reported as an unknown outcome and not as nothing changed.</summary>
    private sealed class RecoveryProgress
    {
        internal bool CommitAttempted { get; set; }
    }

    /// <summary>The store's own minting subtype for the recovery witness; no other type may derive from the seam.</summary>
    private sealed class RecoveryMint : OperatorRecoveryTransition.MintingSource
    {
        internal static OperatorRecoveryTransition Reset(
            DbConnection connection, DbTransaction transaction, DbDataReader row, PlatformAdmission admission) =>
            FromResetUpdate(connection, transaction, row, admission);
    }
}

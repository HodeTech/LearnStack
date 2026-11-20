using System.Data.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>The operator's fenced replay of one dead-lettered subscriber delivery (ADR-0103 § 2).</summary>
public sealed partial class DeliveryDispatchStore
{
    // Every reason a delivery can be dead-lettered with. invalid_envelope is among them so that a fence that names it
    // meets its own refusal (NotRecoverable) and not a stale fence: it is the one reason no replay can act on.
    private static readonly string[] DeadLetterReasons =
        ["unknown_type", "retired_type", "invalid_envelope", "retry_exhausted", "invalid_subscription"];

    /// <summary>
    /// Resets exactly one terminal delivery, under the fence the operator read, and writes the MUST result row on the
    /// same transaction. The inbox identity, every sibling delivery and the producer row are untouched.
    /// </summary>
    /// <remarks>
    /// The row is locked and compared with the fence first: a mismatch is <see cref="OperatorRecoveryOutcome.StaleFence"/>.
    /// A matching fence on a row that is processed, leased, <c>invalid_envelope</c>, no longer an Active ordinary
    /// subscription of its stored type, or whose tenant is not serving is
    /// <see cref="OperatorRecoveryOutcome.NotRecoverable"/>. A refusal writes no row and changes nothing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The admission is not for a replay, is stale or was spent.</exception>
    /// <exception cref="OperatorRecoveryUnavailableException">The lane or the database was unavailable, or the commit's outcome is unknown.</exception>
    /// <exception cref="AuditWriteFailedException">The MUST row could not be written; nothing changed.</exception>
    public async Task<OperatorRecoveryResult> ReplayAsync(
        DeliveryReplayRequest request, PlatformAdmission admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admission);
        OperatorRecoveryAdmission.Consume(admission, PlatformOperations.DeliveryReplay, _clock);
        if (request.TenantId == Guid.Empty || request.TenantId == TenantId.PlatformSentinel.Value
            || request.EventId == Guid.Empty || !OutboxEventRegistry.ValidSubscriptionId(request.SubscriptionId))
        {
            return new OperatorRecoveryResult(OperatorRecoveryOutcome.NotFound);
        }

        if (request.ReplayCount < 0 || !DeadLetterReasons.Contains(request.TerminalReason, StringComparer.Ordinal))
        {
            // No stored row can carry this fence, so it is stale by construction.
            return new OperatorRecoveryResult(OperatorRecoveryOutcome.StaleFence);
        }

        var progress = new RecoveryProgress();
        try
        {
            return await ReplayCoreAsync(request, admission, progress, cancellationToken).ConfigureAwait(false);
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

    private async Task<OperatorRecoveryResult> ReplayCoreAsync(
        DeliveryReplayRequest request, PlatformAdmission admission,
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
        int replayCount;
        await using (var rowLock = new NpgsqlCommand($"""
            SELECT type, processed_at IS NOT NULL, dead_lettered_at, terminal_reason, replay_count,
                locked_by IS NOT NULL OR locked_until IS NOT NULL
            FROM event_deliveries WHERE {Identity} FOR UPDATE
            """, connection, transaction))
        {
            Configure(rowLock);
            AddIdentity(rowLock, request.TenantId, request.SubscriptionId, request.EventId);
            await using var reader = await rowLock.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                return new OperatorRecoveryResult(OperatorRecoveryOutcome.NotFound);
            }

            type = reader.GetString(0);
            processed = reader.GetBoolean(1);
            deadLettered = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
            reason = reader.IsDBNull(3) ? null : reader.GetString(3);
            replayCount = reader.GetInt32(4);
            leased = reader.GetBoolean(5);
        }

        if (deadLettered is not { } deadLetteredAt || deadLetteredAt.UtcTicks != request.DeadLetteredAt.UtcTicks
            || replayCount != request.ReplayCount || !string.Equals(reason, request.TerminalReason, StringComparison.Ordinal))
        {
            return new OperatorRecoveryResult(OperatorRecoveryOutcome.StaleFence);
        }

        var refusal = processed ? "processed"
            : leased ? "leased"
            : reason == "invalid_envelope" ? "invalid_envelope"
            : !_registry.IsActiveOrdinarySubscription(type, request.SubscriptionId) ? "subscription_not_active"
            : !await TenantServesAsync(connection, transaction, request.TenantId, deadline.Token).ConfigureAwait(false)
                ? "tenant_not_serving"
                : null;
        if (refusal is not null)
        {
            return new OperatorRecoveryResult(OperatorRecoveryOutcome.NotRecoverable, Refusal: refusal);
        }

        OperatorRecoveryTransition witness;
        await using (var reset = new NpgsqlCommand($"""
            UPDATE event_deliveries SET terminal_reason = NULL, dead_lettered_at = NULL,
                attempts = 0, last_error = NULL, locked_by = NULL, locked_until = NULL,
                available_after = clock_timestamp(), replay_count = replay_count + 1,
                replayed_at = clock_timestamp()
            WHERE {Identity} AND processed_at IS NULL AND dead_lettered_at = @dead AND terminal_reason = @reason
                AND replay_count = @count AND locked_by IS NULL AND locked_until IS NULL AND type = @type
            RETURNING event_id, tenant_id, organization_id, subscription_id, old.replay_count, old.dead_lettered_at,
                old.terminal_reason, new.replay_count
            """, connection, transaction))
        {
            Configure(reset);
            AddIdentity(reset, request.TenantId, request.SubscriptionId, request.EventId);
            reset.Parameters.AddWithValue("dead", deadLetteredAt);
            reset.Parameters.AddWithValue("reason", request.TerminalReason);
            reset.Parameters.AddWithValue("count", request.ReplayCount);
            reset.Parameters.AddWithValue("type", type);
            await using var reader = await reset.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                return new OperatorRecoveryResult(OperatorRecoveryOutcome.StaleFence);
            }

            witness = RecoveryMint.Replay(connection, transaction, reader, admission);
        }

        await WriteRecoveryRowAsync(witness, admission, connection, transaction, deadline.Token).ConfigureAwait(false);
        progress.CommitAttempted = true;
        try
        {
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is NpgsqlException or OperationCanceledException or IOException)
        {
            // COMMIT may have reached PostgreSQL. The change and its row commit together or not at all, so there is
            // nothing to repair: the operator lists again (a landed replay leaves the dead-lettered list) or reads the
            // tenant's audit row by the correlation id.
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
        internal static OperatorRecoveryTransition Replay(
            DbConnection connection, DbTransaction transaction, DbDataReader row, PlatformAdmission admission) =>
            FromReplayUpdate(connection, transaction, row, admission);
    }
}

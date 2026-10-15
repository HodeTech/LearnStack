using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Owns the fresh application transaction that durably accepts all subscriptions.</summary>
public sealed class PostgresDeliveryAcceptance(
    IUnitOfWork unitOfWork, ITenantContext tenantContext, OutboxEventRegistry registry)
{
    private const string InsertSql = """
        INSERT INTO event_deliveries (
            tenant_id, subscription_id, event_id, organization_id, occurred_at,
            correlation_id, causation_id, actor_user_id, type, topic, partition_key, payload)
        VALUES (@tenant_id, @subscription_id, @event_id, @organization_id, @occurred_at,
                @correlation_id, @causation_id, @actor_user_id, @type, @topic, @partition_key, @payload)
        ON CONFLICT (tenant_id, subscription_id, event_id) DO NOTHING
        RETURNING TRUE
        """;

    // A separate statement gets a fresh READ COMMITTED snapshot after a concurrent
    // INSERT finishes. A same-statement CTE could see the conflict but miss its row.
    private const string MatchesSql = """
        SELECT organization_id IS NOT DISTINCT FROM @organization_id
            AND occurred_at = @occurred_at
            AND correlation_id = @correlation_id
            AND causation_id IS NOT DISTINCT FROM @causation_id
            AND actor_user_id IS NOT DISTINCT FROM @actor_user_id
            AND topic = @topic AND partition_key = @partition_key
            AND payload = @payload,
            type
        FROM event_deliveries
        WHERE tenant_id = @tenant_id AND subscription_id = @subscription_id AND event_id = @event_id
        """;

    internal async Task AcceptAsync(
        IntegrationEventEnvelope envelope,
        OutboxEventContract.SerializedEvent serialized,
        IReadOnlyList<OutboxSubscriptionManifest> subscriptions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (subscriptions.Count == 0
            || subscriptions.Any(subscription => subscription.Readiness != OutboxSubscriptionReadiness.Active
                || subscription.HandlerType is null || !OutboxEventRegistry.ValidSubscriptionId(subscription.Id)))
        {
            throw new InvalidOperationException("Acceptance requires declared Active subscriptions.");
        }

        if (unitOfWork.HasActiveTransaction || unitOfWork.IsRollbackOnly)
        {
            throw new InvalidOperationException("Acceptance must own a fresh application transaction.");
        }

        var frame = await unitOfWork.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var failed = false;
        try
        {
            if (!frame.IsOwner)
            {
                throw new InvalidOperationException("Acceptance cannot join a publisher transaction.");
            }

            await unitOfWork.SetTenantContextAsync(tenantContext, cancellationToken).ConfigureAwait(false);
            var announcement = RequireAnnouncement(serialized, envelope);
            if (unitOfWork.Transaction is not NpgsqlTransaction transaction
                || unitOfWork.Connection is not NpgsqlConnection connection
                || !ReferenceEquals(transaction.Connection, connection))
            {
                throw new InvalidOperationException("Acceptance requires the ambient Npgsql transaction.");
            }

            foreach (var subscription in subscriptions)
            {
                RequireSameTransaction(announcement, transaction, serialized, envelope);
                await using var insert = CreateCommand(InsertSql, connection, transaction, subscription.Id, serialized, envelope);
                if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                {
                    continue;
                }

                RequireSameTransaction(announcement, transaction, serialized, envelope);
                await using var matches = CreateCommand(MatchesSql, connection, transaction, subscription.Id, serialized, envelope);
                await using var retained = await matches.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                // Canonical identity and explicitly compatible aliases name one
                // contract. Every other immutable envelope field must still match.
                if (!await retained.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || !retained.GetBoolean(0)
                    || registry.Resolve(retained.GetString(1)).EventType != envelope.Event.GetType())
                {
                    // Hidden cross-organization conflicts are refusals too. Do not
                    // reveal the original envelope or overwrite scheduling state.
                    throw new InvalidOperationException("The delivery identity already has a different or inaccessible envelope.");
                }
            }

            RequireSameTransaction(announcement, transaction, serialized, envelope);
            await frame.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            failed = true;
            unitOfWork.MarkRollbackOnly();
            try
            {
                await frame.FailAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup cannot replace the SQL failure or unknown COMMIT outcome.
            }

            throw;
        }
        finally
        {
            try
            {
                await frame.DisposeAsync().ConfigureAwait(false);
            }
            catch when (failed)
            {
                // The producer retries the original failure, including unknown COMMIT.
            }
        }
    }

    private TenantContextAnnouncement RequireAnnouncement(
        OutboxEventContract.SerializedEvent serialized, IntegrationEventEnvelope envelope)
    {
        var announcement = unitOfWork.GetTenantContextAnnouncement(unitOfWork.Transaction);
        if (!unitOfWork.HasActiveTransaction || unitOfWork.IsRollbackOnly
            || unitOfWork.Mode != TransactionMode.ReadWrite
            || announcement is null || announcement.Generation <= 0
            || announcement.TenantId.Value != serialized.TenantId
            || announcement.OrganizationId?.Value != envelope.OrganizationId
            || !tenantContext.IsResolved || tenantContext.TenantId != announcement.TenantId
            || tenantContext.OrganizationId != announcement.OrganizationId)
        {
            throw new InvalidOperationException("Acceptance requires its live, matching writable tenant announcement.");
        }

        return announcement;
    }

    private void RequireSameTransaction(
        TenantContextAnnouncement announcement,
        NpgsqlTransaction transaction,
        OutboxEventContract.SerializedEvent serialized,
        IntegrationEventEnvelope envelope)
    {
        if (RequireAnnouncement(serialized, envelope) != announcement
            || !ReferenceEquals(unitOfWork.Transaction, transaction)
            || !ReferenceEquals(transaction.Connection, unitOfWork.Connection))
        {
            throw new InvalidOperationException("The acceptance transaction changed during publication.");
        }
    }

    private static NpgsqlCommand CreateCommand(
        string sql, NpgsqlConnection connection, NpgsqlTransaction transaction,
        string subscriptionId, OutboxEventContract.SerializedEvent serialized,
        IntegrationEventEnvelope envelope)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, serialized.TenantId);
        command.Parameters.AddWithValue("subscription_id", NpgsqlDbType.Text, subscriptionId);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, serialized.EventId);
        command.Parameters.AddWithValue("organization_id", NpgsqlDbType.Uuid, (object?)envelope.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, serialized.OccurredAt.ToUniversalTime());
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Text, envelope.CorrelationId);
        command.Parameters.AddWithValue("causation_id", NpgsqlDbType.Uuid, (object?)envelope.CausationId ?? DBNull.Value);
        command.Parameters.AddWithValue("actor_user_id", NpgsqlDbType.Uuid, (object?)envelope.ActorUserId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("type", NpgsqlDbType.Text, serialized.TypeName);
        command.Parameters.AddWithValue("topic", NpgsqlDbType.Text, serialized.Topic);
        command.Parameters.AddWithValue("partition_key", NpgsqlDbType.Text, serialized.PartitionKey);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, serialized.Payload);
        return command;
    }
}

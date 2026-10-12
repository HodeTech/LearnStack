using LearnStack.Application.Messaging;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Inserts immediately on the admitted producer's ambient transaction.</summary>
public sealed class PostgresOutbox(IUnitOfWork unitOfWork, ProducerExecution producer) : IOutbox
{
    private const string InsertSql =
        """
        INSERT INTO outbox_messages (
            id, occurred_at, tenant_id, organization_id, correlation_id, causation_id,
            actor_user_id, type, topic, partition_key, payload, metadata)
        VALUES (
            @id, @occurred_at, @tenant_id, @organization_id, @correlation_id, @causation_id,
            @actor_user_id, @type, @topic, @partition_key, @payload, NULL)
        """;

    /// <inheritdoc />
    public async Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        cancellationToken.ThrowIfCancellationRequested();
        // Refuse raw/unannounced/read-only/stale scopes before executing event getters
        // or touching Connection (whose getter would otherwise open a connection).
        var announcement = producer.RequireAnnouncement(unitOfWork);
        var envelope = producer.CreateEnvelope(integrationEvent);
        var serialized = OutboxEventContract.Serialize(integrationEvent);
        if (serialized.TenantId != announcement.TenantId.Value)
        {
            throw new ArgumentException("The event tenant must equal the live announcement.", nameof(integrationEvent));
        }

        if (unitOfWork.Transaction is not NpgsqlTransaction transaction
            || unitOfWork.Connection is not NpgsqlConnection connection
            || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException("Outbox persistence requires the ambient Npgsql connection and transaction.");
        }

        await using var command = new NpgsqlCommand(InsertSql, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, serialized.EventId);
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, serialized.OccurredAt.ToUniversalTime());
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, serialized.TenantId);
        command.Parameters.AddWithValue("organization_id", NpgsqlDbType.Uuid, (object?)envelope.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Text, envelope.CorrelationId);
        command.Parameters.AddWithValue("causation_id", NpgsqlDbType.Uuid, (object?)envelope.CausationId ?? DBNull.Value);
        command.Parameters.AddWithValue("actor_user_id", NpgsqlDbType.Uuid, (object?)envelope.ActorUserId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("type", NpgsqlDbType.Text, serialized.TypeName);
        command.Parameters.AddWithValue("topic", NpgsqlDbType.Text, serialized.Topic);
        command.Parameters.AddWithValue("partition_key", NpgsqlDbType.Text, serialized.PartitionKey);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, serialized.Payload);

        // Serialization is user code. It cannot replace this transaction or its
        // announcement and then borrow the authority checked before serialization.
        var live = producer.RequireAnnouncement(unitOfWork);
        if (live != announcement || live.TenantId.Value != serialized.TenantId
            || !ReferenceEquals(unitOfWork.Transaction, transaction))
        {
            throw new InvalidOperationException("The producer announcement changed before outbox INSERT.");
        }

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            unitOfWork.MarkRollbackOnly();
            throw;
        }

        producer.RecordProducedWork();
    }
}

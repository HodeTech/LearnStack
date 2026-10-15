using LearnStack.Application.Messaging;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>The single application-role delivery completion participant.</summary>
internal sealed class PostgresDeliveryCompletion : DeliveryCompletionParticipant
{
    private readonly IUnitOfWork _unit;
    private readonly DeliveryClaim _claim;
    private readonly TenantContextAnnouncement _announcement;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private const string IdentityPredicate = """
        tenant_id = @tenant_id AND subscription_id = @subscription_id AND event_id = @event_id
        AND organization_id IS NOT DISTINCT FROM @organization_id AND locked_by = @token
        AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
        """;

    internal PostgresDeliveryCompletion(IUnitOfWork unit, DeliveryCommitGate gate, DeliveryClaim claim)
        : base(unit, gate)
    {
        _unit = unit;
        _claim = claim;
        _announcement = RequireAnnouncement();
        _transaction = unit.Transaction as NpgsqlTransaction
            ?? throw new InvalidOperationException("Delivery requires the application transaction.");
        _connection = unit.Connection as NpgsqlConnection
            ?? throw new InvalidOperationException("Delivery requires the application connection.");
        RequireCurrent();
    }

    internal async Task<StoredEventEnvelope?> ReadEnvelopeAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        await using var command = CreateCommand("""
            SELECT event_id, tenant_id, organization_id, type, topic, partition_key,
                   occurred_at, correlation_id, causation_id, actor_user_id, payload::text
            FROM event_deliveries WHERE
            """ + " " + IdentityPredicate + " AND locked_until > clock_timestamp()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new StoredEventEnvelope(
            reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.GetString(10));
    }

    protected override async Task FenceAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        // Acquire the row lock without evaluating time. A lease could expire
        // while an unchanged row is locked; evaluate clock_timestamp only in
        // the separate UPDATE after this lock has actually been acquired.
        await using (var rowLock = CreateCommand(
            "SELECT TRUE FROM event_deliveries WHERE " + IdentityPredicate + " FOR UPDATE"))
        {
            if (await rowLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The delivery completion fence refused ownership.");
        }

        RequireCurrent();
        await using var complete = CreateCommand(
            "UPDATE event_deliveries SET processed_at = clock_timestamp() WHERE "
            + IdentityPredicate + " AND locked_until > clock_timestamp() RETURNING TRUE");
        if (await complete.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("The delivery completion fence refused ownership.");
        // Token and expiry remain inert provenance. App grants permit only processed_at.
    }

    private TenantContextAnnouncement RequireAnnouncement()
    {
        var announcement = _unit.GetTenantContextAnnouncement(_unit.Transaction);
        if (!_unit.HasActiveTransaction || _unit.IsRollbackOnly || _unit.Mode != TransactionMode.ReadWrite
            || announcement is null || announcement.Generation <= 0
            || announcement.TenantId.Value != _claim.TenantId
            || announcement.OrganizationId?.Value != _claim.OrganizationId)
            throw new InvalidOperationException("Delivery requires its matching writable tenant announcement.");
        return announcement;
    }

    private void RequireCurrent()
    {
        if (RequireAnnouncement() != _announcement || !ReferenceEquals(_unit.Transaction, _transaction)
            || !ReferenceEquals(_transaction.Connection, _connection)
            || !ReferenceEquals(_unit.Connection, _connection))
            throw new InvalidOperationException("The delivery transaction changed during execution.");
    }

    private NpgsqlCommand CreateCommand(string sql)
    {
        var command = new NpgsqlCommand(sql, _connection, _transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, _claim.TenantId);
        command.Parameters.AddWithValue("subscription_id", NpgsqlDbType.Text, _claim.SubscriptionId);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, _claim.EventId);
        command.Parameters.AddWithValue("organization_id", NpgsqlDbType.Uuid, (object?)_claim.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("token", NpgsqlDbType.Text, _claim.Token);
        return command;
    }
}

using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Claims an inbox identity on the already announced business transaction.</summary>
public sealed class PostgresInboxGuard(IUnitOfWork unitOfWork, ITenantContext tenantContext) : IInboxGuard
{
    private const string InsertSql = """
        INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
        VALUES (@tenant_id, @subscription_id, @event_id)
        ON CONFLICT (tenant_id, subscription_id, event_id) DO NOTHING
        RETURNING TRUE
        """;

    public async Task<bool> TryClaimAsync(
        TenantId tenantId,
        string subscriptionId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty
            || tenantId == TenantId.PlatformSentinel)
        {
            throw new ArgumentException("An inbox claim requires a real tenant.", nameof(tenantId));
        }

        if (!OutboxEventRegistry.ValidSubscriptionId(subscriptionId))
        {
            throw new ArgumentException("An inbox claim requires a stable subscription identity.", nameof(subscriptionId));
        }

        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An inbox claim requires an event identity.", nameof(eventId));
        }

        // Validate before reading Connection, which otherwise opens it lazily.
        var announcement = RequireAnnouncement(tenantId);
        if (unitOfWork.Transaction is not NpgsqlTransaction transaction
            || unitOfWork.Connection is not NpgsqlConnection connection
            || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException("An inbox claim requires the ambient Npgsql transaction.");
        }

        await using var command = new NpgsqlCommand(InsertSql, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId.Value);
        command.Parameters.AddWithValue("subscription_id", NpgsqlDbType.Text, subscriptionId);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        if (RequireAnnouncement(tenantId) != announcement
            || !ReferenceEquals(unitOfWork.Transaction, transaction)
            || !ReferenceEquals(transaction.Connection, unitOfWork.Connection))
        {
            throw new InvalidOperationException("The inbox transaction changed before the claim.");
        }

        try
        {
            // PostgreSQL uniqueness waits for a concurrent uncommitted claim.
            // Commit returns false; rollback allows this transaction's new claim.
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        catch
        {
            unitOfWork.MarkRollbackOnly();
            throw;
        }
    }

    private TenantContextAnnouncement RequireAnnouncement(TenantId tenantId)
    {
        var announcement = unitOfWork.GetTenantContextAnnouncement(unitOfWork.Transaction);
        if (!unitOfWork.HasActiveTransaction || unitOfWork.IsRollbackOnly
            || unitOfWork.Mode != TransactionMode.ReadWrite
            || announcement is null || announcement.Generation <= 0
            || announcement.TenantId != tenantId
            || !tenantContext.IsResolved || tenantContext.TenantId != tenantId
            || tenantContext.OrganizationId != announcement.OrganizationId)
        {
            throw new InvalidOperationException("An inbox claim requires its live, matching writable tenant announcement.");
        }

        return announcement;
    }
}

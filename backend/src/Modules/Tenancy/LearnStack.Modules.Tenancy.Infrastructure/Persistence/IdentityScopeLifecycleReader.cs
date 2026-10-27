using System.Data;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>One uncached statement over Tenancy's own tables, on the caller's announced app transaction.</summary>
public sealed class IdentityScopeLifecycleReader(IUnitOfWork unit) : IIdentityScopeLifecycleReader
{
    public async Task<IdentityScopeLifecycle> ReadAsync(TenantId tenantId, OrganizationId? organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty || tenantId == TenantId.PlatformSentinel
            || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.Transaction is not { IsolationLevel: IsolationLevel.ReadCommitted } transaction
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announced
            || announced.TenantId != tenantId || announced.OrganizationId != organizationId)
            throw new InfrastructureException("Identity scope lifecycle is unavailable.");

        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT t.deleted_at IS NULL,
                       t.deleted_at IS NULL AND t.status IN ('Trial', 'Active'),
                       o.id IS NOT NULL,
                       o.id IS NOT NULL AND o.deleted_at IS NULL AND o.status = 'Active',
                       current_setting('transaction_isolation') = 'read committed'
                FROM public.tenants AS t
                LEFT JOIN public.organizations AS o
                  ON o.tenant_id = t.id AND o.id = @organization
                WHERE t.id = @tenant
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)transaction) { CommandTimeout = 10 };
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, organizationId is { } org
                ? org.Value : DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return new(false, false, false, false);
            if (!reader.GetBoolean(4)) throw new InfrastructureException("Identity scope lifecycle is unavailable.");
            return new(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
        }
        catch (NpgsqlException)
        {
            throw new InfrastructureException("Identity scope lifecycle is unavailable.");
        }
    }
}

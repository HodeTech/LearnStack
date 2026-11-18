using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// ADR-0099's registry on the current tenant's announced owner. Every read goes through
/// <see cref="FederationConnectionReads.Live"/>; a write saves exactly one domain step, because the storage guard
/// admits one row version per UPDATE.
/// </summary>
public sealed class FederationConnectionStore(IdentityDbContext db, IUnitOfWork unit, ITenantContext context,
    ContextualIdentityStorageControl control) : IFederationConnectionStore
{
    private IQueryable<FederationConnection> Live => db.FederationConnections.Live().Where(row => row.TenantId == context.TenantId);

    public async Task<FederationConnection?> FindLiveLockedAsync(CancellationToken cancellationToken)
    {
        Writable();
        control.PrepareFreshRead();
        try
        {
            // Retained exclusion only; the tracked aggregate is materialized afterward by its own statement.
            await using var command = new NpgsqlCommand($"""
                SELECT public.fn_identity_assert_tenant_fence(@tenant,false);
                SELECT id FROM public.federation_connections
                WHERE tenant_id=@tenant AND {FederationConnectionReads.LiveSql} FOR NO KEY UPDATE;
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("tenant", context.TenantId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException failure) when (failure.SqlState == "P0301")
        {
            unit.MarkRollbackOnly();
            throw new IdentityFoundationStorageException(IdentityFoundationStorageFailure.Plan);
        }
        catch (NpgsqlException)
        {
            unit.MarkRollbackOnly();
            throw new IdentityStorageProviderException();
        }
        return await ContextualIdentityStorageControl.ReadAsync(() => Live.SingleOrDefaultAsync(cancellationToken));
    }

    public async Task<FederationConnection?> ReadLiveAsync(CancellationToken cancellationToken)
    {
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        if (unit is IOperationReadScopeView { IsTenantWideRead: true })
            throw new InfrastructureException("Federation connection storage unavailable.");
        return await ContextualIdentityStorageControl.ReadAsync(() => Live.AsNoTracking().SingleOrDefaultAsync(cancellationToken));
    }

    public Task AddAsync(FederationConnection aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Writable();
        db.FederationConnections.Add(aggregate);
        return control.SaveAsync(cancellationToken);
    }

    public Task UpdateAsync(FederationConnection aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (db.Entry(aggregate).State == EntityState.Detached) throw new InvalidOperationException("Federation connection must be tracked.");
        return control.SaveAsync(cancellationToken);
    }

    private void Writable()
    {
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        if (unit.Mode != TransactionMode.ReadWrite || unit is IOperationReadScopeView { IsTenantWideRead: true })
            throw new InfrastructureException("Federation connection storage unavailable.");
    }
}

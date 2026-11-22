using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// <see cref="IDataExportTicketStore"/> on the ambient Identity context and transaction, confined to the announced group
/// (ADR-0107 § 4). A ticket is inserted with its two digests and its one later change is the consumed marker, which is
/// the application role's only update privilege on the table.
/// </summary>
public sealed class DataExportTicketStore(IdentityDbContext db, IUnitOfWork unit, ITenantContext tenant) : IDataExportTicketStore
{
    public async Task AddAsync(DataExportTicket aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Admit();
        if (aggregate.TenantId != tenant.TenantId || aggregate.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("A download ticket is minted only in its own announced group.");
        }

        db.DataExportTickets.Add(aggregate);
        await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(DataExportTicket aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        RequireOwner();
        if (aggregate.TenantId != tenant.TenantId || aggregate.OrganizationId != tenant.OrganizationId
            || db.Entry(aggregate).State == EntityState.Detached
            || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), unit.Transaction))
        {
            throw new InvalidOperationException("A download ticket is saved only from its own loaded group.");
        }

        await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataExportTicket?> FindLockedAsync(DataExportArtifactId artifactId, string digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        Admit();
        if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigitLower))
        {
            return null;
        }

        // The digest is unique per tenant; the artifact and group narrow it further, so another artifact's or group's
        // ticket is absent here rather than refused.
        await using var command = DataRequestSql.Command(unit, tenant, """
            SELECT id FROM public.data_export_tickets
            WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization AND artifact_id=@artifact AND digest=@digest
            FOR UPDATE
            """);
        command.Parameters.AddWithValue("artifact", artifactId.Value);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Bytea, Convert.FromHexString(digest));
        var found = await DataRequestSql.ExecuteAsync(unit, token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false);
        if (found is not Guid id)
        {
            return null;
        }

        var ticketId = DataExportTicketId.From(id);
        return await db.DataExportTickets.SingleAsync(candidate => candidate.Id == ticketId
            && candidate.TenantId == tenant.TenantId && candidate.OrganizationId == tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountOpenAsync(DataExportArtifactId artifactId, CancellationToken cancellationToken)
    {
        Admit();
        // Expiry by the database clock, as the ticket guard judges it; the tenant and artifact lead the ticket index.
        await using var command = DataRequestSql.Command(unit, tenant, """
            SELECT count(*) FROM public.data_export_tickets
            WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization AND artifact_id=@artifact
              AND consumed_at IS NULL AND clock_timestamp() < expires_at
            """);
        command.Parameters.AddWithValue("artifact", artifactId.Value);
        var found = await DataRequestSql.ExecuteAsync(unit, token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false);
        return found is long count ? checked((int)count) : throw new InfrastructureException("Data request storage unavailable.");
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request storage unavailable.");
        }
    }

    /// <summary>The announced writable owner, with the clean Identity context re-enrolled on its transaction.</summary>
    private void Admit()
    {
        RequireOwner();
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
    }

    private void RequireOwner()
    {
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { } announcement
            || announcement.TenantId != tenant.TenantId || announcement.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("Download ticket storage requires its announced writable owner.");
        }
    }
}

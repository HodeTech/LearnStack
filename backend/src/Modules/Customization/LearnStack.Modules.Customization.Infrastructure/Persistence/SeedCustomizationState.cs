using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

public sealed class SeedCustomizationState(CustomizationDbContext db, IUnitOfWork unit) : ISeedCustomizationState
{
    public async Task PrepareAsync(bool taxonomy, string key, Guid? rootId, bool registration, CancellationToken cancellationToken)
    {
        RequireOwner();
        if (registration && !taxonomy && key != BuiltInCustomizations.Card.Key)
            await CustomizationConceptHistory.LockRegistrationsAsync(db, cancellationToken);
        await CustomizationConceptHistory.LockAsync(db, taxonomy, key, cancellationToken);
        if (rootId is not { } id) return;
        var sql = taxonomy
            ? "SELECT id FROM public.tenant_level_taxonomies WHERE tenant_id = @tenant AND id = @id FOR UPDATE"
            : "SELECT id FROM public.tenant_content_types WHERE tenant_id = @tenant AND id = @id FOR UPDATE";
        RequireOwner();
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
        { CommandTimeout = 10 };
        command.Parameters.AddWithValue("tenant", db.CurrentTenantId.Value);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenantContentType>> ReadContentTypesAsync(CancellationToken cancellationToken)
    {
        RequireOwner();
        return await db.TenantContentTypes.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenantLevelTaxonomy>> ReadTaxonomiesAsync(CancellationToken cancellationToken)
    {
        RequireOwner();
        return await db.TenantLevelTaxonomies.AsNoTracking().Include(row => row.Items).ToListAsync(cancellationToken);
    }

    private void RequireOwner()
    {
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != db.CurrentTenantId || db.CurrentTenantWideRead)
            throw new InvalidOperationException("Fixed content verification requires its ordinary owner.");
    }
}

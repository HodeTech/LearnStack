using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.Invitations;

public sealed class InvitationAdministrationStore(IdentityDbContext db, IUnitOfWork unit, ITenantContext context,
    ContextualIdentityStorageControl control) : IInvitationAdministrationStore
{
    private IQueryable<Invitation> Scoped => db.Invitations.Where(row => row.TenantId == context.TenantId
        && row.OrganizationId == context.OrganizationId && row.DeletedAt == null && row.DeletedBy == null);
    private void Admit()
    {
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        if (unit.Mode != TransactionMode.ReadWrite || unit is IOperationReadScopeView { IsTenantWideRead: true })
            throw new InfrastructureException("Invitation administration storage unavailable.");
    }
    public async Task<Invitation?> ReadAsync(InvitationId id, CancellationToken cancellationToken)
    {
        Admit();
        return await ContextualIdentityStorageControl.ReadAsync(() => Scoped.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken));
    }
    public async Task<IReadOnlyList<Invitation>> ListAsync(InvitationId? after, int take, CancellationToken cancellationToken)
    {
        Admit();
        if (take is < 1 or > 101) throw new ArgumentOutOfRangeException(nameof(take));
        var source = after is { } anchor ? db.Invitations.FromSqlInterpolated($"SELECT * FROM public.invitations WHERE id > {anchor.Value}") : db.Invitations;
        return await ContextualIdentityStorageControl.ReadAsync(() => source.AsNoTracking().Where(row => row.TenantId == context.TenantId
            && row.OrganizationId == context.OrganizationId && row.DeletedAt == null && row.DeletedBy == null)
            .OrderBy(row => row.Id).Take(take).ToArrayAsync(cancellationToken));
    }
    public async Task<IReadOnlyList<Role>> ReadRolesAsync(CancellationToken cancellationToken)
    {
        Admit();
        return await ContextualIdentityStorageControl.ReadAsync(() => db.Roles.AsNoTracking().Include(row => row.Grants)
            .Where(row => row.TenantId == context.TenantId && row.DeletedAt == null && row.DeletedBy == null)
            .OrderBy(row => row.Id).Take(9).ToArrayAsync(cancellationToken));
    }
    public async Task<Invitation?> FindLockedAsync(InvitationId id, CancellationToken cancellationToken)
    {
        Admit();
        control.PrepareFreshRead();
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT public.fn_identity_assert_tenant_fence(@tenant,false);
                SELECT id FROM public.invitations WHERE tenant_id=@tenant AND id=@id
                AND organization_id IS NOT DISTINCT FROM @org FOR NO KEY UPDATE;
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("tenant", context.TenantId.Value);
            command.Parameters.AddWithValue("id", id.Value);
            command.Parameters.AddWithValue("org", NpgsqlDbType.Uuid, (object?)context.OrganizationId?.Value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return await Scoped.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        }
        catch (NpgsqlException) { unit.MarkRollbackOnly(); throw new InfrastructureException("Invitation administration storage unavailable."); }
    }
    public async Task<IReadOnlyList<Invitation>> FindPendingIssuedLockedAsync(UserId inviter, int take, CancellationToken cancellationToken)
    {
        Admit();
        if (take is < 1 or > 101) throw new ArgumentOutOfRangeException(nameof(take));
        control.PrepareFreshRead();
        try
        {
            // ix_invitations_inviter_user_id narrows to the person's issued offers; the tenant, group and status filter
            // that short list. The rows stay locked against a concurrent administrator until the erasure commits.
            await using var command = new NpgsqlCommand("""
                SELECT public.fn_identity_assert_tenant_fence(@tenant,false);
                SELECT id FROM public.invitations WHERE inviter_user_id=@inviter AND tenant_id=@tenant
                AND organization_id IS NOT DISTINCT FROM @org AND status='Pending' AND deleted_at IS NULL
                ORDER BY id LIMIT @take FOR NO KEY UPDATE;
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("tenant", context.TenantId.Value);
            command.Parameters.AddWithValue("inviter", inviter.Value);
            command.Parameters.AddWithValue("org", NpgsqlDbType.Uuid, (object?)context.OrganizationId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("take", take);
            var ids = new List<InvitationId>(take);
            await using (var row = await command.ExecuteReaderAsync(cancellationToken))
            {
                await row.NextResultAsync(cancellationToken);
                while (await row.ReadAsync(cancellationToken)) ids.Add(InvitationId.From(row.GetGuid(0)));
            }
            if (ids.Count == 0) return [];
            return await Scoped.Where(row => ids.Contains(row.Id) && row.Status == InvitationStatus.Pending)
                .OrderBy(row => row.Id).ToArrayAsync(cancellationToken);
        }
        catch (NpgsqlException) { unit.MarkRollbackOnly(); throw new InfrastructureException("Invitation administration storage unavailable."); }
    }
    public Task AddAsync(Invitation aggregate, CancellationToken cancellationToken = default)
    {
        Admit(); db.Invitations.Add(aggregate); return control.SaveAsync(cancellationToken);
    }
    public Task UpdateAsync(Invitation aggregate, CancellationToken cancellationToken = default)
    {
        if (db.Entry(aggregate).State == EntityState.Detached) throw new InvalidOperationException("Invitation must be tracked.");
        return control.SaveAsync(cancellationToken);
    }
}

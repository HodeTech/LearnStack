using LearnStack.Application.Messaging;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using NpgsqlTypes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Invitations;

public sealed class InvitationEmailJobStore(IdentityDbContext db, IUnitOfWork unit, ProducerExecution producer, ITenantContext tenant) : IInvitationEmailJobStore
{
    private Invitation? _loaded;
    public BackgroundJobId RequireCurrentJob()
    {
        var origin = producer.CaptureJobOrigin(unit);
        if (origin.ParentJobId is not { } job || origin.OperationType != typeof(PrepareTenantInvitationEmailCommand)
            && origin.OperationType != typeof(PrepareOrganizationInvitationEmailCommand)
            && origin.OperationType != typeof(FinalizeTenantInvitationEmailCommand)
            && origin.OperationType != typeof(FinalizeOrganizationInvitationEmailCommand))
            throw new InvalidOperationException("Invitation email job owner refused.");
        producer.RequireJobOrigin(origin, unit);
        return job;
    }
    public async Task<Invitation?> FindLockedAsync(InvitationId invitationId, CancellationToken cancellationToken)
    {
        _ = RequireCurrentJob();
        _loaded = null;
        await using var command = new NpgsqlCommand("SELECT id FROM public.invitations WHERE id=@id AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization FOR UPDATE",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", invitationId.Value);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)tenant.OrganizationId?.Value ?? DBNull.Value);
        try
        {
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) return null;
            _loaded = await db.Invitations.SingleOrDefaultAsync(invitation => invitation.Id == invitationId && invitation.TenantId == tenant.TenantId
                && invitation.OrganizationId == tenant.OrganizationId, cancellationToken).ConfigureAwait(false);
            return _loaded;
        }
        catch (NpgsqlException) { throw new InvalidOperationException("Invitation email storage unavailable."); }
    }
    public Task AddAsync(Invitation aggregate, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Invitation email jobs cannot create offers.");

    public async Task UpdateAsync(Invitation aggregate, CancellationToken cancellationToken = default)
    {
        _ = RequireCurrentJob();
        if (!ReferenceEquals(aggregate, _loaded) || aggregate.TenantId != tenant.TenantId || aggregate.OrganizationId != tenant.OrganizationId
            || db.Entry(aggregate).State == EntityState.Detached)
            throw new InvalidOperationException("Invitation email update requires its exact loaded root.");
        // Stale-generation results remain a tracked no-op; never mark the whole root modified.
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateException) { unit.MarkRollbackOnly(); throw new InvalidOperationException("Invitation email storage unavailable."); }
    }
}

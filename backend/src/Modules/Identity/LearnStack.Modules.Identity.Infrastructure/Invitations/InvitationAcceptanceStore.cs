using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
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

public sealed class InvitationAcceptanceReadStore(IdentityDbContext db, IUnitOfWork unit, ITenantContext context,
    ContextualIdentityStorageControl control, IOperationInvocationContext invocations) : IInvitationAcceptanceReadStore, IInvitationDelegationReader
{
    private IQueryable<Invitation> Offers => db.Invitations.Where(row => row.TenantId == context.TenantId
        && row.OrganizationId == context.OrganizationId && row.DeletedAt == null && row.DeletedBy == null);
    private IQueryable<Membership> Members => db.Memberships.Where(row => row.TenantId == context.TenantId
        && row.OrganizationId == context.OrganizationId && row.DeletedAt == null && row.DeletedBy == null
        && (row.Status == MembershipStatus.Active || row.Status == MembershipStatus.Suspended));
    private void Admit()
    {
        if (invocations.CurrentDecision is not { InvitationWitness: { IsActive: true } witness }
            || !ReferenceEquals(invocations.CurrentInvocation, witness.Invocation)
            || unit is not IInvitationEntryUnitOfWork entry || !entry.IsInvitationEntryOwner(witness)
            || context.Origin != TenantContextOrigin.HostAndInvitationOffer)
            throw new InfrastructureException("Invitation storage requires its exact operation owner.");
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
    }
    public async Task<Invitation?> ReadOfferAsync(InvitationId id, CancellationToken cancellationToken)
    { Admit(); return await ContextualIdentityStorageControl.ReadAsync(() => Offers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken)); }
    public async Task<Membership?> ReadMemberAsync(UserId actor, CancellationToken cancellationToken)
    {
        Admit();
        return await ContextualIdentityStorageControl.ReadAsync(() => Members.AsNoTracking().Include(row => row.Profile)
            .Include(row => row.RoleAssignments).SingleOrDefaultAsync(row => row.UserId == actor, cancellationToken));
    }
    public async Task<Membership?> ReadResultAsync(MembershipId id, UserId actor, CancellationToken cancellationToken)
    {
        Admit();
        return await ContextualIdentityStorageControl.ReadAsync(() => Members.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == id && row.UserId == actor && row.Status == MembershipStatus.Active, cancellationToken));
    }
    public async Task LockInvitationAsync(InvitationId id, CancellationToken cancellationToken)
    {
        Admit();
        if (unit.Mode != TransactionMode.ReadWrite) throw new InfrastructureException("Invitation locking requires its writable owner.");
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
        }
        catch (NpgsqlException) { unit.MarkRollbackOnly(); throw new IdentityStorageProviderException(); }
    }
    public async Task<InvitationAcceptanceGraph> ReadFinalAsync(InvitationId id, UserId actor, CancellationToken cancellationToken)
    {
        Admit();
        control.PrepareFreshRead();
        // Exactly one reset: both retained roots survive until the common owner commits.
        var offer = await ContextualIdentityStorageControl.ReadAsync(() => Offers.SingleOrDefaultAsync(row => row.Id == id, cancellationToken));
        var member = await ContextualIdentityStorageControl.ReadAsync(() => Members.Include(row => row.Profile).Include(row => row.RoleAssignments)
            .SingleOrDefaultAsync(row => row.UserId == actor, cancellationToken));
        return new(offer, member);
    }
    public async Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken)
    {
        Admit();
        try
        {
            await using var command = new NpgsqlCommand("SELECT clock_timestamp()", (NpgsqlConnection)unit.Connection,
                (NpgsqlTransaction)unit.Transaction!)
            { CommandTimeout = 5 };
            return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(cancellationToken))!);
        }
        catch (NpgsqlException) { unit.MarkRollbackOnly(); throw new IdentityStorageProviderException(); }
    }
    public async Task<IdentityAuthorizationResult> ReadAsync(UserId storedInviter, CancellationToken cancellationToken)
    {
        Admit();
        if (unit.Mode != TransactionMode.ReadWrite) throw new InfrastructureException("Invitation delegation requires its acceptance owner.");
        // The immutable stored inviter is checked against the exact offer, not an invented HTTP candidate.
        var witness = invocations.CurrentDecision!.InvitationWitness!;
        var inviterMatches = await ContextualIdentityStorageControl.ReadAsync(() => Offers.AsNoTracking().AnyAsync(row =>
            row.Id == InvitationId.From(witness.Offer.InvitationId) && row.InviterUserId == storedInviter, cancellationToken));
        if (!inviterMatches) throw new InfrastructureException("Invitation delegation source refused.");
        var members = await ContextualIdentityStorageControl.ReadAsync(() => db.Memberships.AsNoTracking().Include(row => row.RoleAssignments)
            .Where(row => row.TenantId == context.TenantId && row.UserId == storedInviter && row.Status == MembershipStatus.Active
                && row.DeletedAt == null && row.DeletedBy == null && (row.OrganizationId == null || row.OrganizationId == context.OrganizationId))
            .ToArrayAsync(cancellationToken));
        var roleIds = members.SelectMany(row => row.RoleAssignments).Select(row => row.RoleId).Distinct().ToArray();
        var roles = await ContextualIdentityStorageControl.ReadAsync(() => db.Roles.AsNoTracking().Include(row => row.Grants)
            .Where(row => row.TenantId == context.TenantId && roleIds.Contains(row.Id) && row.DeletedAt == null && row.DeletedBy == null)
            .ToArrayAsync(cancellationToken));
        if (members.Length > 2 || roleIds.Length > 8) throw new InfrastructureException("Invitation delegation graph unavailable.");
        return new(members.Length == 0 ? IdentityAuthorizationOutcome.NotCovered : IdentityAuthorizationOutcome.Covered,
            storedInviter, context.TenantId, context.OrganizationId,
            [.. members.Select(member => new AuthorizationMembershipFacts(member.Id.Value, member.OrganizationId,
                [.. roles.Where(role => member.RoleAssignments.Any(assignment => assignment.RoleId == role.Id))
                    .Select(role => new AuthorizationRoleFacts(role.Id.Value, role.BuiltInKey, role.PermissionScope,
                        [.. role.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey))]))]))]);
    }
}

/// <summary>Only the acceptance transition is saved here; physical COMMIT belongs to the common coordinator.</summary>
public sealed class InvitationWriteStore(IdentityDbContext db, IUnitOfWork unit, IOperationInvocationContext invocations,
    ContextualIdentityStorageControl control)
    : IInvitationWriteStore
{
    public Task AddAsync(Invitation aggregate, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Invitation acceptance cannot create an offer.");
    public async Task UpdateAsync(Invitation aggregate, CancellationToken cancellationToken = default)
    {
        control.RequireMutableGraph();
        var witness = invocations.CurrentDecision?.InvitationWitness;
        if (witness is null || !ReferenceEquals(invocations.CurrentInvocation, witness.Invocation)
            || unit is not IInvitationEntryUnitOfWork entry || !entry.IsInvitationEntryOwner(witness)
            || unit.Mode != TransactionMode.ReadWrite || witness.Offer.InvitationId != aggregate.Id.Value
            || db.Entry(aggregate).State == EntityState.Detached || aggregate.Status != InvitationStatus.Accepted
            || db.ChangeTracker.Entries().Any(row => row.State is EntityState.Added or EntityState.Deleted
                || row.State == EntityState.Modified && !ReferenceEquals(row.Entity, aggregate)))
            throw new InvalidOperationException("Invitation acceptance save refused.");
        var savepoints = db.Database.AutoSavepointsEnabled;
        db.Database.AutoSavepointsEnabled = false;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        { unit.MarkRollbackOnly(); throw new IdentityFoundationStorageException(IdentityFoundationStorageFailure.Conflict); }
        // This exact save admits only one Accepted Invitation UPDATE. Its owned final clock/pin
        // trigger uses bare 23514; no other graph or physical COMMIT reaches this translation.
        catch (DbUpdateException failure) when (failure.InnerException is PostgresException { SqlState: "23514" or "23505" or "23503" })
        { unit.MarkRollbackOnly(); throw new IdentityFoundationStorageException(IdentityFoundationStorageFailure.Conflict); }
        catch (Exception failure) when (failure is DbUpdateException or NpgsqlException or TimeoutException)
        { unit.MarkRollbackOnly(); throw new IdentityStorageProviderException(); }
        finally { db.Database.AutoSavepointsEnabled = savepoints; }
    }
}

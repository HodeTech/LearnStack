using System.Data.Common;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

public sealed class MembershipCustomFieldStore(IdentityDbContext db, ContextualIdentityStorageControl control, IUnitOfWork unit)
    : IMembershipCustomFieldStore
{
    private MembershipId? _lockedMembership;
    private DbTransaction? _lockedTransaction;
    private Membership? _loadedMembership;

    public async Task LockAsync(MembershipId membershipId, CancellationToken cancellationToken = default)
    {
        _loadedMembership = null;
        await control.LockMembershipAsync(membershipId, cancellationToken);
        _lockedMembership = membershipId;
        _lockedTransaction = unit.Transaction;
    }

    public async Task<Membership?> ReadForWriteAsync(MembershipId membershipId, CancellationToken cancellationToken = default)
    {
        RequireOwner();
        if (_lockedMembership != membershipId) throw new InvalidOperationException("Membership lock target does not match.");
        _loadedMembership = null;
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        // Lock acquisition already enrolls the clean announced owner. Refresh only
        // this root; clearing the tracker would invalidate other captured graphs.
        var tracked = db.ChangeTracker.Entries().Where(entry => entry.Entity switch
        {
            Membership member => member.Id == membershipId,
            MembershipProfile profile => profile.MembershipId == membershipId,
            MembershipRoleAssignment assignment => assignment.MembershipId == membershipId,
            _ => false,
        }).ToArray();
        if (tracked.Any(entry => entry.State != EntityState.Unchanged))
            throw new InvalidOperationException("A changed Membership cannot be refreshed.");
        foreach (var entry in tracked) entry.State = EntityState.Detached;
        _loadedMembership = await ContextualIdentityStorageControl.ReadAsync(() => db.Memberships.Include(member => member.Profile)
            .Include(member => member.RoleAssignments).SingleOrDefaultAsync(member => member.Id == membershipId, cancellationToken));
        return _loadedMembership;
    }

    public Task AddAsync(Membership aggregate, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The custom field store cannot create Memberships.");

    public async Task UpdateAsync(Membership aggregate, CancellationToken cancellationToken = default)
    {
        RequireOwner();
        if (aggregate is null || !ReferenceEquals(aggregate, _loadedMembership) || aggregate.Id != _lockedMembership
            || db.Entry(aggregate).State is EntityState.Detached or EntityState.Added or EntityState.Deleted)
            throw new InvalidOperationException("Membership custom fields require the exact locked, tracked root.");
        await control.SaveAsync(cancellationToken);
        await control.ValidateGraphAsync(cancellationToken);
    }

    private void RequireOwner()
    {
        if (_lockedMembership is null || _lockedTransaction is null || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.Mode != TransactionMode.ReadWrite || !ReferenceEquals(unit.Transaction, _lockedTransaction))
            throw new InvalidOperationException("Membership custom fields require their locked owner.");
    }
}

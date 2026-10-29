using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Uses the current ordinary owner, real model filters and RLS for contextual profile disclosure.</summary>
public sealed class MembershipAdministrationReader(IdentityDbContext db, IUnitOfWork unit, ITenantContext context)
    : IMembershipAdministrationReader
{
    public async Task<Membership?> ReadDetailAsync(MembershipId membershipId, CancellationToken cancellationToken = default)
    {
        var wide = Admit(detail: true);
        var query = db.Memberships.AsNoTracking().Include(member => member.Profile).Include(member => member.RoleAssignments)
            .Where(member => member.Id == membershipId && member.TenantId == context.TenantId
                && member.DeletedAt == null && (member.Status == MembershipStatus.Active || member.Status == MembershipStatus.Suspended));
        if (!wide) query = query.Where(member => member.OrganizationId == context.OrganizationId);
        var result = await ContextualIdentityStorageControl.ReadAsync(() => query.SingleOrDefaultAsync(cancellationToken));
        if (Admit(detail: true) != wide) throw Refused();
        return result;
    }

    public async Task<Membership?> ReadOwnAsync(CancellationToken cancellationToken = default)
    {
        Admit(detail: false);
        var result = await ContextualIdentityStorageControl.ReadAsync(() => db.Memberships.AsNoTracking().Include(member => member.Profile)
            .SingleOrDefaultAsync(member => member.TenantId == context.TenantId && member.UserId == context.UserId!.Value
                && member.OrganizationId == context.OrganizationId && member.Status == MembershipStatus.Active && member.DeletedAt == null,
                cancellationToken));
        Admit(detail: false);
        return result;
    }

    private bool Admit(bool detail)
    {
        var wide = unit is IOperationReadScopeView { IsTenantWideRead: true };
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.UserId is null
            || context.UserId == UserId.SystemActor || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || (detail ? unit.Mode != TransactionMode.ReadWrite
                : unit.Mode is not (TransactionMode.ReadOnly or TransactionMode.ReadWrite) || wide)
            || db.CurrentTenantWideRead != wide || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != context.TenantId || announcement.OrganizationId != context.OrganizationId)
            throw Refused();
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        return wide;
    }

    private static TenantContextMissingException Refused() =>
        new("Membership administration requires its clean announced ordinary owner.");
}

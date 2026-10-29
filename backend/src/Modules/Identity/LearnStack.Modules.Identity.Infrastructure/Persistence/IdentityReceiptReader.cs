using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Ordinary scoped exact-ID receipt subjects; no historical child or global User query.</summary>
public sealed class IdentityReceiptReader(IdentityDbContext db, IUnitOfWork unit, ITenantContext context) : IIdentityReceiptReader
{
    public Task<Membership?> ReadMembershipAsync(MembershipId membershipId, CancellationToken cancellationToken = default) =>
        ReadAsync(() => db.Memberships.AsNoTracking().SingleOrDefaultAsync(member => member.Id == membershipId
            && member.TenantId == context.TenantId && member.OrganizationId == context.OrganizationId, cancellationToken));

    public Task<Role?> ReadRoleAsync(RoleId roleId, CancellationToken cancellationToken = default) =>
        ReadAsync(() => db.Roles.AsNoTracking().SingleOrDefaultAsync(role => role.Id == roleId
            && role.TenantId == context.TenantId, cancellationToken));

    private async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        Admit();
        var result = await ContextualIdentityStorageControl.ReadAsync(read);
        Admit();
        return result;
    }

    private void Admit()
    {
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel
            || context.UserId is null || context.UserId == UserId.SystemActor
            || unit.Mode != TransactionMode.ReadWrite || unit is IOperationReadScopeView { IsTenantWideRead: true }
            || db.CurrentTenantWideRead)
            throw new TenantContextMissingException("Identity receipt disclosure requires its ordinary scoped writable owner.");
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
    }
}

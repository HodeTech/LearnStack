using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

public sealed class MembershipAdmissionReader(IdentityDbContext db, IUnitOfWork unit, ITenantContext context)
    : IMembershipAdmissionReader
{
    public async Task<Membership?> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!context.IsResolved || context.UserId is null || context.Origin != TenantContextOrigin.HostAndNativeSessionScope
            || unit.Mode != TransactionMode.ReadWrite || unit.IsRollbackOnly
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announced
            || announced.TenantId != context.TenantId || announced.OrganizationId != context.OrganizationId)
            throw new InfrastructureException("Native membership admission is unavailable.");
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        return await ContextualIdentityStorageControl.ReadAsync(() => db.Memberships.AsNoTracking()
            .Where(member => member.UserId == context.UserId.Value && member.TenantId == context.TenantId
                && member.Status == MembershipStatus.Active && member.DeletedAt == null
                && (member.OrganizationId == null || member.OrganizationId == context.OrganizationId))
            .OrderBy(member => member.OrganizationId == context.OrganizationId ? 0 : 1)
            .ThenBy(member => member.Id).FirstOrDefaultAsync(cancellationToken));
    }
}

using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

public sealed class IdentityAdministrationReadStore(IdentityDbContext db, IUnitOfWork unit, ITenantContext context)
    : IIdentityAdministrationReadStore
{
    public Task<IReadOnlyList<MembershipDirectoryItem>> ReadOrganizationMembersAsync(MembershipId? after, int take,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 101) throw new ArgumentOutOfRangeException(nameof(take));
        if (context.OrganizationId is null) throw new TenantContextMissingException("The branch directory requires an exact organization.");
        return ReadAsync<IReadOnlyList<MembershipDirectoryItem>>(async () =>
        {
            var source = after is { } anchor
                ? db.Memberships.FromSqlInterpolated($"SELECT * FROM memberships WHERE id > {anchor.Value}") : db.Memberships;
            return await source.AsNoTracking().Where(member => member.TenantId == context.TenantId
                    && member.OrganizationId == context.OrganizationId && member.DeletedAt == null && member.DeletedBy == null
                    && (member.Status == MembershipStatus.Active || member.Status == MembershipStatus.Suspended))
                .OrderBy(member => member.Id).Take(take).Select(member => new MembershipDirectoryItem(member.Id.Value,
                    member.UserId, member.OrganizationId,
                    member.Status == MembershipStatus.Active ? DirectoryMembershipStatus.Active : DirectoryMembershipStatus.Suspended,
                    member.Profile.DisplayName, member.Profile.PreferredLocale)).ToArrayAsync(cancellationToken);
        });
    }

    public Task<IReadOnlyList<Role>> ReadRolesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<Role>>(async () => await db.Roles.AsNoTracking()
            .Where(role => role.TenantId == context.TenantId && role.DeletedAt == null && role.DeletedBy == null)
            .OrderBy(role => role.Id).Take(9).ToArrayAsync(cancellationToken));

    public Task<Role?> ReadRoleAsync(RoleId roleId, CancellationToken cancellationToken = default) =>
        ReadAsync(() => db.Roles.AsNoTracking().Include(role => role.Grants).SingleOrDefaultAsync(role => role.Id == roleId
            && role.TenantId == context.TenantId && role.DeletedAt == null && role.DeletedBy == null, cancellationToken));

    private async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        Admit();
        var result = await ContextualIdentityStorageControl.ReadAsync(read);
        Admit();
        return result;
    }

    private void Admit()
    {
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.UserId is null
            || context.UserId == UserId.SystemActor || unit.Mode != TransactionMode.ReadWrite
            || unit is IOperationReadScopeView { IsTenantWideRead: true } || db.CurrentTenantWideRead)
            throw new TenantContextMissingException("Identity administration reads require their ordinary scoped writable owner.");
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
    }
}

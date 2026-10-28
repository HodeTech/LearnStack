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

/// <summary>Uses the real model filters and physical tenant-read owner; no filter bypass or synthetic scope.</summary>
public sealed class MembershipDirectoryReader(IdentityDbContext db, IUnitOfWork unit, ITenantContext context)
    : IMembershipDirectoryReader
{
    public async Task<IReadOnlyList<MembershipDirectoryItem>> ReadAsync(MembershipId? after, int take,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 101) throw new ArgumentOutOfRangeException(nameof(take));
        Admit();
        // Keep the seek on the converted UUID in SQL; the resulting entity source
        // still composes with the real model filters and the six-field projection.
        var source = after is { } anchor
            ? db.Memberships.FromSqlInterpolated($"SELECT * FROM memberships WHERE id > {anchor.Value}")
            : db.Memberships;
        var rows = source.AsNoTracking().Where(member => member.TenantId == context.TenantId
            && member.DeletedAt == null && (member.Status == MembershipStatus.Active || member.Status == MembershipStatus.Suspended));
        var result = await ContextualIdentityStorageControl.ReadAsync(() => rows.OrderBy(member => member.Id).Take(take)
            .Select(member => new MembershipDirectoryItem(member.Id.Value, member.UserId, member.OrganizationId,
                member.Status == MembershipStatus.Active ? DirectoryMembershipStatus.Active : DirectoryMembershipStatus.Suspended,
                member.Profile.DisplayName, member.Profile.PreferredLocale)).ToArrayAsync(cancellationToken));
        Admit();
        return result;
    }

    private void Admit()
    {
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.UserId is null
            || context.UserId == UserId.SystemActor || unit is not IOperationReadScopeView { IsTenantWideRead: true }
            || unit.Mode != TransactionMode.ReadWrite || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != context.TenantId || announcement.OrganizationId != context.OrganizationId)
            throw new TenantContextMissingException("The membership directory requires its announced ordinary tenant-read owner.");
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
    }
}

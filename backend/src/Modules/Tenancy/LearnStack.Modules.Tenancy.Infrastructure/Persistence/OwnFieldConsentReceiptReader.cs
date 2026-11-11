using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Read one original audit subject on the already admitted scoped writable owner.</summary>
public sealed class OwnFieldConsentReceiptReader(TenancyDbContext db, IUnitOfWork unit, ITenantContext context)
    : IOwnFieldConsentReceiptReader
{
    public async Task<MembershipFieldConsent?> ReadAsync(UserId actor, IReadOnlyCollection<Guid> memberships,
        Guid evidenceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        Admit(actor, memberships, evidenceId);
        var ids = memberships.Distinct().ToArray();
        using var boundary = TenancyAdministrationStorageScope.Enter(db);
        var target = await TenancyAdministrationStorageBoundary.ReadAsync(() => db.MembershipFieldConsents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == MembershipFieldConsentId.From(evidenceId)
                && row.TenantId == context.TenantId && row.OrganizationId == context.OrganizationId
                && row.SubjectUserId == actor && ids.Contains(row.MembershipId), cancellationToken));
        Admit(actor, memberships, evidenceId);
        return target;
    }

    private void Admit(UserId actor, IReadOnlyCollection<Guid> memberships, Guid evidenceId)
    {
        if (!context.IsResolved || context.UserId != actor || !StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor
            || !StronglyTypedId.IsAssigned(context.TenantId) || context.TenantId == TenantId.PlatformSentinel
            || evidenceId == Guid.Empty || evidenceId.Version != 7 || memberships.Count is < 1 or > 20
            || memberships.Any(id => id == Guid.Empty || id.Version != 7)
            || !unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || unit is IOperationReadScopeView { IsTenantWideRead: true } || db.CurrentTenantWideRead
            || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } scope
            || scope.TenantId != context.TenantId || scope.OrganizationId != context.OrganizationId
            || db.CurrentTenantId != context.TenantId || db.CurrentOrganizationId != context.OrganizationId
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection))
            throw new TenancyAdministrationStorageException(TenancyAdministrationStorageFailure.Provider);
        if (db.Database.CurrentTransaction is not { } current || !ReferenceEquals(current.GetDbTransaction(), unit.Transaction))
            db.Database.UseTransaction(unit.Transaction);
    }
}

using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Settings;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

public sealed class InvitationPolicyReader(TenancyDbContext db, IUnitOfWork unit, ITenantContext context)
    : IInvitationPolicyReader, IInvitationMessageContextReader
{
    private void Admit(bool orgless)
    {
        if (!context.IsResolved || context.UserId is null || context.UserId == UserId.SystemActor
            || orgless && context.OrganizationId is not null || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || unit is IOperationReadScopeView { IsTenantWideRead: true }
            || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } scope
            || scope.TenantId != context.TenantId || scope.OrganizationId != context.OrganizationId
            || db.CurrentTenantId != context.TenantId || db.CurrentOrganizationId != context.OrganizationId
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection))
            throw new InfrastructureException("Invitation policy storage unavailable.");
        if (db.Database.CurrentTransaction is not { } current || !ReferenceEquals(current.GetDbTransaction(), unit.Transaction))
            db.Database.UseTransaction(unit.Transaction);
    }
    public async Task<TenantSetting?> ReadAsync(CancellationToken cancellationToken = default)
    {
        Admit(true);
        return await ReadSafeAsync(() => db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == context.TenantId && row.OrganizationId == null && row.Key == TenantSettingKeys.InvitationLifetimeName
            && row.DeletedAt == null && row.DeletedBy == null, cancellationToken));
    }
    public async Task<TenantSetting?> ReadReceiptAsync(TenantSettingId id, CancellationToken cancellationToken = default)
    {
        Admit(true);
        return await ReadSafeAsync(() => db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == id && row.TenantId == context.TenantId && row.OrganizationId == null
            && row.Key == TenantSettingKeys.InvitationLifetimeName, cancellationToken));
    }
    async Task<Result<InvitationMessageContext>> IInvitationMessageContextReader.ReadAsync(CancellationToken cancellationToken)
    {
        Admit(false);
        var result = await ReadSafeAsync(async () =>
        {
            var tenant = await db.Tenants.AsNoTracking().Include(row => row.Locales).SingleOrDefaultAsync(row =>
                row.Id == context.TenantId && row.DeletedAt == null && row.DeletedBy == null
                && (row.Status == TenantStatus.Trial || row.Status == TenantStatus.Active), cancellationToken);
            if (tenant is null) return null;
            var host = await db.PlatformHostMappings.AsNoTracking().Where(row => row.TenantId == context.TenantId && row.IsActive
                && (row.OrganizationId == context.OrganizationId || row.OrganizationId == null))
                .OrderByDescending(row => row.OrganizationId != null).ThenBy(row => row.Host).Select(row => row.Host).FirstOrDefaultAsync(cancellationToken);
            var locale = tenant.Locales.Where(row => row.IsEnabled && (row.Locale == "en" || row.Locale == "tr"))
                .OrderByDescending(row => row.IsDefault).ThenBy(row => row.Sort).Select(row => row.Locale).FirstOrDefault();
            return host is null || locale is null || EffectiveHost.Normalize(host) != host ? null
                : new InvitationMessageContext(host, locale, tenant.DisplayName);
        });
        return result is null ? Result<InvitationMessageContext>.Fail(new Error(LocalizedMessage.Of("lockey_not_found"))) : Result.Ok(result);
    }
    private async Task<T> ReadSafeAsync<T>(Func<Task<T>> query)
    {
        using var boundary = TenancyAdministrationStorageScope.Enter(db);
        try { return await TenancyAdministrationStorageBoundary.ReadAsync(query); }
        catch (LearnStack.Modules.Tenancy.Application.Abstractions.TenancyAdministrationStorageException)
        { unit.MarkRollbackOnly(); throw new InfrastructureException("Invitation policy storage unavailable."); }
    }

}

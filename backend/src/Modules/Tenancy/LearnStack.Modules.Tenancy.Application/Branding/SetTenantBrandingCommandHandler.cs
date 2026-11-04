using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.Modules.Tenancy.Application.Seeding;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Branding;

internal sealed class SetTenantBrandingCommandHandler(ITenantSettingWriteStore settings, ITenantExistenceReader tenants, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock, SeedTenantWriteGuard seedGuard)
    : IRequestHandler<SetTenantBrandingCommand, Result<TenantBrandingDto>>
{
    public async Task<Result<TenantBrandingDto>> Handle(SetTenantBrandingCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TenantWriteFailures.Scope<TenantBrandingDto>(tenantContext) is { } scopeFailure)
        {
            return scopeFailure;
        }

        var admission = await seedGuard.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<TenantBrandingDto>.Fail(admission.Error);
        if (!await tenants.ExistsAsync(cancellationToken))
        {
            return TenantWriteFailures.Code<TenantBrandingDto>("lockey_not_found");
        }

        var theme = BrandingThemeRegistry.ValidateAndCanonicalize(request.Theme);
        if (theme.IsFailure)
        {
            return Result<TenantBrandingDto>.Fail(theme.Error);
        }

        var setting = await settings.FindForUpdateAsync(TenantSettingId.From(request.SettingId), cancellationToken);
        if (setting is not null)
            return TenantWriteFailures.Field<TenantBrandingDto>("lockey_business_rule_violation", "SettingId", "lockey_identifier_taken");
        if (await seedGuard.VerifyAsync(admission.Value, request, cancellationToken) is { IsFailure: true } changed)
            return Result<TenantBrandingDto>.Fail(changed.Error);
        setting = TenantSetting.Create(TenantSettingId.From(request.SettingId), tenantContext.TenantId, null,
            BrandingThemeRegistry.SettingKey, theme.Value, clock, tenantContext.UserId ?? UserId.SystemActor);

        auditSubject.Designate(setting);
        try
        {
            await settings.AddAsync(setting, cancellationToken);
        }
        catch (AggregateConcurrencyException)
        {
            unitOfWork.MarkRollbackOnly();
            return TenantWriteFailures.Code<TenantBrandingDto>("lockey_concurrency_conflict");
        }
        catch (AggregateConflictException conflict) when (conflict.ConstraintName is
            "pk_tenant_settings" or "ux_tenant_settings_tenant_id_organization_id_key")
        {
            unitOfWork.MarkRollbackOnly();
            return TenantWriteFailures.Field<TenantBrandingDto>("lockey_business_rule_violation", "SettingId", "lockey_setting_taken");
        }

        return Result.Ok(new TenantBrandingDto(setting.Id.Value, setting.Version, setting.Value));
    }
}

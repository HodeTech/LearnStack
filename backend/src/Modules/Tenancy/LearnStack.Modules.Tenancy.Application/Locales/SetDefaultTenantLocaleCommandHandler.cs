using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Locales;

internal sealed class SetDefaultTenantLocaleCommandHandler(ITenantWriteStore tenants, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock)
    : IRequestHandler<SetDefaultTenantLocaleCommand, Result<TenantLocalesDto>>
{
    public async Task<Result<TenantLocalesDto>> Handle(SetDefaultTenantLocaleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TenantWriteFailures.Scope<TenantLocalesDto>(tenantContext) is { } scopeFailure)
        {
            return scopeFailure;
        }

        var tenant = await tenants.FindAsync(tenantContext.TenantId, cancellationToken);
        if (tenant is null)
        {
            return TenantWriteFailures.Code<TenantLocalesDto>("lockey_not_found");
        }

        auditSubject.Designate(tenant);
        if (LocaleWriteSupport.Preflight(tenant, request.ExpectedVersion) is { } failure)
        {
            return failure;
        }

        var target = tenant.Locales.SingleOrDefault(locale => locale.Locale == LocaleTag.Canonicalize(request.Locale));
        if (target is null || !target.IsEnabled)
        {
            return TenantWriteFailures.Field<TenantLocalesDto>("lockey_validation_failed", "Locale",
                target is null ? "lockey_locale_not_found" : "lockey_locale_disabled");
        }

        tenant.SetDefaultLocale(target.Locale, clock, tenantContext.UserId ?? UserId.SystemActor);
        return await LocaleWriteSupport.SaveAsync(tenant, tenants, unitOfWork, cancellationToken);
    }
}

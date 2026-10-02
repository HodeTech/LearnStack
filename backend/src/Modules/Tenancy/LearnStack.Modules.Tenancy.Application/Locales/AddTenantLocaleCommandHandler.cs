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

internal sealed class AddTenantLocaleCommandHandler(ITenantWriteStore tenants, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock)
    : IRequestHandler<AddTenantLocaleCommand, Result<TenantLocalesDto>>
{
    public async Task<Result<TenantLocalesDto>> Handle(AddTenantLocaleCommand request, CancellationToken cancellationToken)
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

        if (tenant.Locales.Any(locale => locale.Locale == LocaleTag.Canonicalize(request.Locale)))
        {
            return TenantWriteFailures.Field<TenantLocalesDto>("lockey_business_rule_violation", "Locale", "lockey_locale_taken");
        }

        tenant.AddLocale(request.Locale, request.IsDefault, clock, tenantContext.UserId ?? UserId.SystemActor,
            request.IsEnabled, request.Sort);
        return await LocaleWriteSupport.SaveAsync(tenant, tenants, unitOfWork, cancellationToken);
    }
}

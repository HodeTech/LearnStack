using System.Collections.Immutable;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Locales;

internal static class LocaleWriteSupport
{
    internal static Result<TenantLocalesDto>? Preflight(Domain.Tenant tenant, long expectedVersion) =>
        tenant.Version != expectedVersion
            ? TenantWriteFailures.Code<TenantLocalesDto>("lockey_concurrency_conflict")
            : !tenant.HasValidLocaleConfiguration()
                ? TenantWriteFailures.Field<TenantLocalesDto>("lockey_validation_failed", "Locale", "lockey_locale_configuration_invalid")
                : null;

    internal static async Task<Result<TenantLocalesDto>> SaveAsync(Domain.Tenant tenant, ITenantWriteStore store,
        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        try
        {
            await store.UpdateAsync(tenant, cancellationToken);
        }
        catch (AggregateConcurrencyException)
        {
            unitOfWork.MarkRollbackOnly();
            return TenantWriteFailures.Code<TenantLocalesDto>("lockey_concurrency_conflict");
        }
        catch (AggregateConflictException conflict) when (conflict.ConstraintName is
            "pk_tenant_locales" or "ux_tenant_locales_tenant_id_is_default")
        {
            // A mutation or first two-pass save may already exist. An absorbed nested
            // refusal must not flush it later from the ambient tracker.
            unitOfWork.MarkRollbackOnly();
            return TenantWriteFailures.Field<TenantLocalesDto>("lockey_business_rule_violation", "Locale", "lockey_locale_taken");
        }

        return Result.Ok(new TenantLocalesDto(tenant.Id, tenant.Version, tenant.Locales
            .OrderBy(locale => locale.Sort).ThenBy(locale => locale.Locale, StringComparer.Ordinal)
            .Select(locale => new TenantLocaleDto(locale.Locale, locale.IsEnabled, locale.IsDefault, locale.Sort))
            .ToImmutableArray()));
    }
}

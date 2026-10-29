using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Tenancy.Application.Administration;

/// <summary>Default and contained locale changes write only the Tenant root.</summary>
public abstract class TenantAdministrationCommandHandlerBase(ITenantWriteStore store,
    ITenancyAdministrationReader reader, TenancyAdministrationAuthorization authorization, ITenantContext context,
    IAuditSubject subject, IClock clock, IGuidFactory guids)
{
    protected async Task<Result<long>> ExecuteAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var admission = await authorization.AdmitWriteAsync(request, cancellationToken);
            if (admission.IsFailure) return Result<long>.Fail(admission.Error);
            var target = await store.FindForUpdateAsync(context.TenantId, cancellationToken);
            var check = await authorization.VerifyWriteAsync(admission.Value, cancellationToken);
            if (check.IsFailure) return Result<long>.Fail(check.Error);
            if (target is null || target.Id != context.TenantId || target.IsDeleted || target.DeletedBy is not null
                || target.Status is not (TenantStatus.Trial or TenantStatus.Active)) return Fail("lockey_not_found");
            subject.Designate(target);
            var version = ExpectedVersion(request);
            if (target.Version != version) return Fail("lockey_concurrency_conflict");
            var prepared = await PrepareAsync(request, target, cancellationToken);
            if (request is SelectDefaultOrganizationCommand)
            {
                check = await authorization.VerifyWriteAsync(admission.Value, cancellationToken);
                if (check.IsFailure) return Result<long>.Fail(check.Error);
            }
            if (prepared.IsFailure) return prepared;
            Mutate(request, target);
            if (target.Version != version) await store.UpdateAsync(target, cancellationToken);
            return Result.Ok(target.Version);
        }
        catch (Exception failure) when (failure is TenancyAdministrationStorageException or IdentityMutationLockException
            or AggregateConcurrencyException or AggregateConflictException)
        {
            return authorization.StorageFailure<long>(failure);
        }
    }

    private async Task<Result<long>> PrepareAsync(object request, Domain.Tenant tenant, CancellationToken cancellationToken)
    {
        if (request is SelectDefaultOrganizationCommand selection)
        {
            var organization = await reader.ReadOrganizationAsync(selection.OrganizationId, cancellationToken);
            return organization is { IsDeleted: false, Status: OrganizationStatus.Active }
                && organization.Id == selection.OrganizationId && organization.TenantId == tenant.Id
                ? Result.Ok(tenant.Version) : Fail("lockey_not_found");
        }
        if (!tenant.HasValidLocaleConfiguration()) return LocaleFailure("lockey_validation_failed", "lockey_locale_configuration_invalid");
        var locale = CanonicalLocale(request);
        var existing = tenant.Locales.SingleOrDefault(item => item.Locale == locale);
        if (request is AddManagedTenantLocaleCommand)
            return existing is null ? Result.Ok(tenant.Version) : LocaleFailure("lockey_business_rule_violation", "lockey_locale_taken");
        if (existing is null) return Fail("lockey_not_found");
        return request switch
        {
            SetManagedDefaultTenantLocaleCommand when !existing.IsEnabled => LocaleFailure("lockey_business_rule_violation", "lockey_locale_disabled"),
            SetTenantLocaleEnabledCommand { IsEnabled: false } when existing.IsDefault => Fail("lockey_business_rule_violation"),
            RemoveTenantLocaleCommand when existing.IsDefault => Fail("lockey_business_rule_violation"),
            _ => Result.Ok(tenant.Version),
        };
    }

    private void Mutate(object request, Domain.Tenant tenant)
    {
        var actor = context.UserId!.Value;
        switch (request)
        {
            case SelectDefaultOrganizationCommand selection:
                if (tenant.DefaultOrganizationId != selection.OrganizationId)
                    tenant.AssignDefaultOrganization(selection.OrganizationId, clock, guids, actor);
                break;
            case AddManagedTenantLocaleCommand add:
                tenant.AddLocale(CanonicalLocale(request), add.IsDefault, clock, actor, add.IsEnabled, add.Sort);
                break;
            case SetManagedDefaultTenantLocaleCommand:
                var locale = CanonicalLocale(request);
                if (!tenant.Locales.Any(item => item.Locale == locale && item.IsDefault)) tenant.SetDefaultLocale(locale, clock, actor);
                break;
            case SetTenantLocaleEnabledCommand enable:
                tenant.SetLocaleEnabled(CanonicalLocale(request), enable.IsEnabled, clock, actor);
                break;
            case RemoveTenantLocaleCommand:
                tenant.RemoveLocale(CanonicalLocale(request), clock, actor);
                break;
            default:
                throw new ArgumentException("Tenant administration request is invalid.", nameof(request));
        }
    }

    private static long ExpectedVersion(object request) => request switch
    {
        SelectDefaultOrganizationCommand command => command.ExpectedVersion,
        AddManagedTenantLocaleCommand command => command.ExpectedVersion,
        SetManagedDefaultTenantLocaleCommand command => command.ExpectedVersion,
        SetTenantLocaleEnabledCommand command => command.ExpectedVersion,
        RemoveTenantLocaleCommand command => command.ExpectedVersion,
        _ => throw new ArgumentException("Tenant administration request is invalid.", nameof(request)),
    };

    private static string CanonicalLocale(object request) => LocaleTag.Canonicalize(request switch
    {
        AddManagedTenantLocaleCommand command => command.Locale,
        SetManagedDefaultTenantLocaleCommand command => command.Locale,
        SetTenantLocaleEnabledCommand command => command.Locale,
        RemoveTenantLocaleCommand command => command.Locale,
        _ => throw new ArgumentException("Locale administration request is invalid.", nameof(request)),
    });

    private static Result<long> LocaleFailure(string key, string reason) => TenantWriteFailures.Field<long>(key, "Locale", reason);
    private static Result<long> Fail(string key) => TenancyAdministrationFailures.Code<long>(key);
}

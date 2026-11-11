using System.Data.Common;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

public sealed class CustomFieldLocaleConfigurationReader(TenancyDbContext context, ITenantContext tenantContext)
    : ICustomFieldLocaleConfigurationReader
{
    public async Task<Result<CustomFieldLocaleConfiguration>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!tenantContext.IsResolved) return Refused();
        using var boundary = TenantLocaleReadScope.Enter(context);
        Tenant? tenant;
        try
        {
            tenant = await context.Tenants.AsNoTracking().Include(row => row.Locales)
                .SingleOrDefaultAsync(row => row.Id == tenantContext.TenantId && row.DeletedAt == null
                    && (row.Status == TenantStatus.Trial || row.Status == TenantStatus.Active), cancellationToken);
        }
        catch (InvalidOperationException failure) when (failure.InnerException is DbException or TimeoutException)
        {
            throw new TenantLocaleEligibilityReadException();
        }
        catch (Exception failure) when (failure is DbException or TimeoutException)
        {
            throw new TenantLocaleEligibilityReadException();
        }
        if (tenant is null || !tenant.HasValidLocaleConfiguration()) return Refused();
        var enabled = tenant.Locales.Where(row => row.IsEnabled).Select(row => row.Locale).ToArray();
        var defaultLocale = tenant.Locales.Single(row => row.IsDefault).Locale;
        return Result.Ok(new CustomFieldLocaleConfiguration(defaultLocale, enabled));
    }

    private static Result<CustomFieldLocaleConfiguration> Refused() =>
        Result<CustomFieldLocaleConfiguration>.Fail(new Error(new LocalizedMessage("lockey_validation_failed")));
}

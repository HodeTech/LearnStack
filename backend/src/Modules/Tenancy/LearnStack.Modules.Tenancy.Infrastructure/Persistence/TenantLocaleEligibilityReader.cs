using System.Data.Common;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

public sealed class TenantLocaleEligibilityReader(TenancyDbContext context, ITenantContext tenantContext)
    : ITenantLocaleEligibilityReader
{
    public async Task<Result<string>> ReadEligibleAsync(string locale, CancellationToken cancellationToken)
    {
        if (!tenantContext.IsResolved || string.IsNullOrEmpty(locale) || locale.Length > LocaleTag.MaxLength)
        {
            return Refused();
        }

        try
        {
            LocaleTag.EnsureWellFormed(locale, nameof(locale));
        }
        catch (ArgumentException)
        {
            return Refused();
        }

        using var boundary = TenantLocaleReadScope.Enter(context);
        Tenant? tenant;
        try
        {
            tenant = await context.Tenants.AsNoTracking().Include(row => row.Locales)
                .SingleOrDefaultAsync(row => row.Id == tenantContext.TenantId && row.DeletedAt == null,
                    cancellationToken);
        }
        catch (InvalidOperationException failure) when (failure.InnerException is DbException or TimeoutException)
        {
            throw new TenantLocaleEligibilityReadException();
        }
        catch (Exception failure) when (failure is DbException or TimeoutException)
        {
            throw new TenantLocaleEligibilityReadException();
        }
        if (tenant is null || !tenant.HasValidLocaleConfiguration())
        {
            return Refused();
        }

        var canonical = LocaleTag.Canonicalize(locale);
        return tenant.Locales.Any(row => row.Locale == canonical && row.IsEnabled)
            ? Result.Ok(canonical) : Refused();
    }

    private static Result<string> Refused() => Result<string>.Fail(new Error(
        new LocalizedMessage("lockey_validation_failed"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            ["Locale"] = [new LocalizedMessage("lockey_locale_invalid")],
        }));
}

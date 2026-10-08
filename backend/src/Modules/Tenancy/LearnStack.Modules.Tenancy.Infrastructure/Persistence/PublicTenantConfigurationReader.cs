using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

public sealed partial class PublicTenantConfigurationReader(
    TenancyDbContext context, ITenantContext tenantContext, IUnitOfWork unit,
    ILogger<PublicTenantConfigurationReader> logger) : IPublicTenantConfigurationReader
{
    public async Task<Result<PublicTenantConfiguration>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!tenantContext.IsResolved || tenantContext.TenantId == TenantId.PlatformSentinel
            || tenantContext.HostScope is not { } host || host.TenantId != tenantContext.TenantId
            || (host.OrganizationId is not null && host.OrganizationId != tenantContext.OrganizationId)
            || unit.Mode != TransactionMode.ReadOnly || !unit.HasActiveTransaction
            || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || context.Database.CurrentTransaction is not { } transaction
            || !ReferenceEquals(transaction.GetDbTransaction(), unit.Transaction))
            throw new TenantContextMissingException("Public configuration requires matching host provenance and an announced, enlisted read-only transaction.");

        var tenant = await context.Tenants.AsNoTracking().Include(row => row.Locales)
            .SingleOrDefaultAsync(row => row.Id == tenantContext.TenantId && row.DeletedAt == null
                && (row.Status == TenantStatus.Trial || row.Status == TenantStatus.Active), cancellationToken);
        if (tenant is null) return Hidden();
        if (host.OrganizationId is { } organization
            && !await context.Organizations.AsNoTracking().AnyAsync(row => row.Id == organization
                && row.TenantId == tenantContext.TenantId && row.DeletedAt == null
                && row.Status == OrganizationStatus.Active, cancellationToken))
            return Hidden();

        if (!tenant.HasValidLocaleConfiguration() || tenant.Locales.Any(row => !IsCanonicalLocale(row.Locale)))
        {
            InvalidStoredConfiguration(logger);
            return Result<PublicTenantConfiguration>.Fail(new Error(new LocalizedMessage("lockey_dependency_unavailable")));
        }

        return Result.Ok(new PublicTenantConfiguration(tenant.DisplayName,
            tenant.Locales.Where(row => row.IsEnabled).OrderBy(row => row.Sort)
                .ThenBy(row => row.Locale, StringComparer.Ordinal).Select(row => row.Locale).ToArray(),
            tenant.Locales.SingleOrDefault(row => row.IsEnabled && row.IsDefault)?.Locale));
    }

    private static bool IsCanonicalLocale(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > LocaleTag.MaxLength) return false;
        try
        {
            LocaleTag.EnsureWellFormed(value, nameof(value));
            return string.Equals(value, LocaleTag.Canonicalize(value), StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    private static Result<PublicTenantConfiguration> Hidden() =>
        Result<PublicTenantConfiguration>.Fail(new Error(new LocalizedMessage("lockey_not_found")));

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Public tenant locale configuration is invalid; explicit remediation is required.")]
    private static partial void InvalidStoredConfiguration(ILogger logger);
}

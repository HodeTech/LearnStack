using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Settings;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

public sealed class TenantSettingsAccessor(
    TenancyDbContext context, ITenantContext tenantContext, IUnitOfWork unit, TenantSettingRegistry registry)
    : ITenantSettingsAccessor
{
    public async Task<Result<TenantSettingRead<T>>> ReadAsync<T>(
        TenantSettingKey<T> key, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (!tenantContext.IsResolved || tenantContext.TenantId == TenantId.PlatformSentinel
            || !unit.HasActiveTransaction || !unit.IsTenantContextIssuedOn(unit.Transaction))
        {
            throw new TenantContextMissingException("Settings reads require a resolved, announced ambient tenant transaction.");
        }

        var registration = registry.Find(key);
        if (registration is null)
        {
            return Invalid<T>();
        }

        var organization = registration.AllowsOrganizationScope ? tenantContext.OrganizationId : null;
        var rows = await context.TenantSettings.AsNoTracking()
            .Where(row => row.TenantId == tenantContext.TenantId && row.Key == registration.Key
                && row.DeletedAt == null
                && (row.OrganizationId == null || (organization != null && row.OrganizationId == organization)))
            .Select(row => new { row.OrganizationId, row.Value })
            .ToListAsync(cancellationToken);
        var selected = rows.SingleOrDefault(row => organization != null && row.OrganizationId == organization)
            ?? rows.SingleOrDefault(row => row.OrganizationId == null);
        if (selected is null)
        {
            return Result.Ok(new TenantSettingRead<T>(null));
        }

        var parsed = registration.Parse(selected.Value);
        return parsed.IsSuccess ? Result.Ok(new TenantSettingRead<T>(parsed.Value)) : Invalid<T>();
    }

    private static Result<TenantSettingRead<T>> Invalid<T>() where T : class =>
        Result<TenantSettingRead<T>>.Fail(new Error(new LocalizedMessage("lockey_validation_failed"),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
            {
                ["Setting"] = [new LocalizedMessage("lockey_invalid_value")],
            }));
}

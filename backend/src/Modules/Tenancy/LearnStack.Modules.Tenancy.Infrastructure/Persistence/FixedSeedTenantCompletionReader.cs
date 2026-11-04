using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Actual retained topology facts; this does not attest global eligibility or seed-profile history.</summary>
public sealed class FixedSeedTenantCompletionReader(TenancyDbContext db, ISeedStateReader state,
    IUnitOfWork unit, ITenantContext context) : ISeedTenantCompletionReader
{
    public async Task<bool> VerifyAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        var declaration = witness.GetDeclaration<SeedTenantDeclaration>();
        var prepared = witness.GetPreparedTarget<SeedAdministrationOriginPreparedTarget>();
        RequireCurrent(witness, declaration);
        var tenant = await state.ReadTenantAsync(cancellationToken);
        RequireCurrent(witness, declaration);
        if (tenant is null || tenant.Id != declaration.TenantId || tenant.Slug != declaration.Slug
            || string.IsNullOrWhiteSpace(tenant.DisplayName) || tenant.DefaultOrganizationId is null
            || tenant.Status is not ("Trial" or "Active" or "Suspended")
            || tenant.AdoptedSeedOriginVersion is not (0 or 1)
            || tenant.Version != prepared.TenantVersion || tenant.AdoptedSeedOriginVersion != prepared.Marker
            || tenant.DefaultOrganizationId != prepared.DefaultOrganizationId
            || tenant.DeletedAt is not null || tenant.DeletedBy is not null
            || !ValidStamps(tenant.CreatedAt, tenant.CreatedBy, tenant.UpdatedAt, tenant.UpdatedBy, tenant.Version)
            || !ValidLocales(tenant)) return false;

        var first = await state.ReadOrganizationAsync(declaration.DefaultOrganization.Id, cancellationToken);
        RequireCurrent(witness, declaration);
        var second = await state.ReadOrganizationAsync(declaration.SecondOrganization.Id, cancellationToken);
        RequireCurrent(witness, declaration);
        if (!OriginalOrganization(first, declaration.DefaultOrganization, declaration.TenantId)
            || !OriginalOrganization(second, declaration.SecondOrganization, declaration.TenantId)) return false;
        var currentDefault = tenant.DefaultOrganizationId == first!.Id ? first
            : tenant.DefaultOrganizationId == second!.Id ? second
            : await state.ReadOrganizationAsync(tenant.DefaultOrganizationId.Value, cancellationToken);
        RequireCurrent(witness, declaration);
        if (currentDefault is null || currentDefault.Id != tenant.DefaultOrganizationId
            || currentDefault.TenantId != tenant.Id || currentDefault.Status != "Active"
            || currentDefault.DeletedAt is not null || currentDefault.DeletedBy is not null) return false;

        var host = await state.ReadHostMappingAsync(declaration.PublicHost, cancellationToken);
        RequireCurrent(witness, declaration);
        if (host is null || host.Host != declaration.PublicHost || host.TenantId != tenant.Id
            || host.OrganizationId != declaration.HostOrganizationId || !host.IsActive || !host.IsPubliclyLive
            || declaration.ThemeId is not { } themeId) return false;
        var theme = await state.ReadSettingAsync(TenantSettingId.From(themeId), cancellationToken);
        RequireCurrent(witness, declaration);
        return theme is not null && theme.Id == themeId && theme.TenantId == tenant.Id
            && theme.OrganizationId is null && theme.Key == BrandingThemeRegistry.SettingKey
            && theme.DeletedAt is null && theme.DeletedBy is null
            && ValidStamps(theme.CreatedAt, theme.CreatedBy, theme.UpdatedAt, theme.UpdatedBy, theme.Version)
            && BrandingThemeRegistry.ValidateAndCanonicalize(theme.Value).IsSuccess;
    }

    private void RequireCurrent(SeedCompletionReadWitness witness, SeedTenantDeclaration declaration)
    {
        witness.RequireCurrent();
        var transaction = unit.Transaction;
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || transaction is null || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || unit is not IOperationReadScopeView { IsTenantWideRead: false }
            || !context.IsResolved || context.TenantId != declaration.TenantId || context.OrganizationId is not null
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), transaction)
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != declaration.TenantId || announcement.OrganizationId is not null
            || !witness.IsExpectedOn(unit, transaction, announcement.Generation))
            throw new InvalidOperationException("The fixed topology completion owner is unavailable.");
    }

    private static bool OriginalOrganization(OrganizationSeedDto? value, SeedOrganizationDeclaration declaration,
        TenantId tenant) => value is not null && value.Id == declaration.Id && value.TenantId == tenant
        && value.Slug == declaration.Slug && !string.IsNullOrWhiteSpace(value.DisplayName)
        && value.Status is "Active" or "Suspended" or "Archived" && value.DeletedAt is null && value.DeletedBy is null
        && ValidStamps(value.CreatedAt, value.CreatedBy, value.UpdatedAt, value.UpdatedBy, value.Version);

    private static bool ValidStamps(DateTimeOffset created, UserId? creator, DateTimeOffset? updated,
        UserId? editor, long version) => created != default && creator == UserId.SystemActor && version >= 0
        && (version == 0 ? updated is null && editor is null
            : updated is { } at && at != default && editor is { } actor && actor.IsInitialized() && actor.Value != Guid.Empty);

    private static bool ValidLocales(TenantSeedDto tenant) => tenant.Locales.Length > 0
        && tenant.Locales.Select(locale => locale.Locale).Distinct(StringComparer.Ordinal).Count() == tenant.Locales.Length
        && tenant.Locales.All(locale => LocaleTag.IsWellFormed(locale.Locale) && (!locale.IsDefault || locale.IsEnabled))
        && tenant.Locales.Any(locale => locale.IsEnabled) && tenant.Locales.Count(locale => locale.IsDefault) == 1;
}

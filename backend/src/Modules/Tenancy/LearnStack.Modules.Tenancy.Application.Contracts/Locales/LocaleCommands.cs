using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Locales;

/// <summary>Canonical locale membership; defaults are always enabled.</summary>
public sealed record TenantLocaleDto(string Locale, bool IsEnabled, bool IsDefault, short Sort);
/// <summary>Committed tenant-root version and immutable membership ordered by sort/locale.</summary>
public sealed record TenantLocalesDto(TenantId TenantId, long Version, ImmutableArray<TenantLocaleDto> Locales);

/// <summary>
/// Adds canonical membership in the trusted tenant-wide context. The first enabled
/// locale becomes default; an explicit enabled default replaces the incumbent.
/// </summary>
/// <param name="ExpectedVersion">Exact current tenant-root version.</param>
/// <param name="Locale">Well-formed locale tag, canonicalized before insertion.</param>
/// <param name="IsEnabled">Whether the locale admits content writes.</param>
/// <param name="IsDefault">Explicit promotion; requires enabled membership.</param>
/// <param name="Sort">Nonnegative presentation order; ties are permitted.</param>
public sealed record AddTenantLocaleCommand(long ExpectedVersion, string Locale,
    bool IsEnabled, bool IsDefault, short Sort) : IRequest<Result<TenantLocalesDto>>;
/// <summary>Promotes an existing enabled locale in the trusted tenant-wide context.</summary>
/// <param name="ExpectedVersion">Exact current tenant-root version.</param>
/// <param name="Locale">Well-formed tag identifying existing canonical membership.</param>
public sealed record SetDefaultTenantLocaleCommand(long ExpectedVersion, string Locale)
    : IRequest<Result<TenantLocalesDto>>;

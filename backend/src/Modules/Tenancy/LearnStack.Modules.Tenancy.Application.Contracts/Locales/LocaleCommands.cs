using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Locales;

public sealed record TenantLocaleDto(string Locale, bool IsEnabled, bool IsDefault, short Sort);
public sealed record TenantLocalesDto(TenantId TenantId, long Version, ImmutableArray<TenantLocaleDto> Locales);

// Unrouted tenant-wide writes. The tenant comes only from the trusted context.
public sealed record AddTenantLocaleCommand(long ExpectedVersion, string Locale,
    bool IsEnabled, bool IsDefault, short Sort) : IRequest<Result<TenantLocalesDto>>;
public sealed record SetDefaultTenantLocaleCommand(long ExpectedVersion, string Locale)
    : IRequest<Result<TenantLocalesDto>>;

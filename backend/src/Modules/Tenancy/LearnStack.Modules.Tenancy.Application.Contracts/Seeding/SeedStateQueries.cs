using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Seeding;

// Trusted contextual verification only: no public marker, endpoint or tenant input.
public sealed record SeedLookup<T>(T? State) where T : class;

public sealed record TenantLocaleSeedDto(string Locale, bool IsEnabled, bool IsDefault, short Sort);
public sealed record TenantSeedDto(TenantId Id, string Slug, string DisplayName, string Status,
    OrganizationId? DefaultOrganizationId, long Version, ImmutableArray<TenantLocaleSeedDto> Locales,
    DateTimeOffset CreatedAt = default, UserId? CreatedBy = null, DateTimeOffset? UpdatedAt = null,
    UserId? UpdatedBy = null, DateTimeOffset? DeletedAt = null, UserId? DeletedBy = null,
    int AdoptedSeedOriginVersion = 0);
public sealed record OrganizationSeedDto(OrganizationId Id, TenantId TenantId, string Slug,
    string DisplayName, string Status, long Version = 0, DateTimeOffset CreatedAt = default,
    UserId? CreatedBy = null, DateTimeOffset? UpdatedAt = null, UserId? UpdatedBy = null,
    DateTimeOffset? DeletedAt = null, UserId? DeletedBy = null);
public sealed record HostMappingSeedDto(string Host, TenantId TenantId, OrganizationId? OrganizationId,
    bool IsActive, bool IsPubliclyLive);
public sealed record SettingSeedDto(Guid Id, TenantId TenantId, OrganizationId? OrganizationId,
    string Key, string Value, long Version, DateTimeOffset CreatedAt = default, UserId? CreatedBy = null,
    DateTimeOffset? UpdatedAt = null, UserId? UpdatedBy = null, DateTimeOffset? DeletedAt = null, UserId? DeletedBy = null);

public sealed record GetTenantSeedStateQuery() : IRequest<Result<SeedLookup<TenantSeedDto>>>;
public sealed record GetOrganizationSeedStateQuery(OrganizationId OrganizationId) : IRequest<Result<SeedLookup<OrganizationSeedDto>>>;
public sealed record GetHostMappingSeedStateQuery(string Host) : IRequest<Result<SeedLookup<HostMappingSeedDto>>>;
public sealed record GetSettingSeedStateQuery(Guid SettingId) : IRequest<Result<SeedLookup<SettingSeedDto>>>;

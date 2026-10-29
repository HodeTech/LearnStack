using System.Collections.Immutable;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Seeding;

public sealed record SeedLookup<T>(T? State) where T : class;

/// <summary>Retained installation evidence, independent of mutable showcase values.</summary>
public sealed record SeedRootEvidence(DateTimeOffset CreatedAt, UserId CreatedBy,
    DateTimeOffset? UpdatedAt, UserId? UpdatedBy);

public sealed record RoleSeedDto(Guid Id, TenantId TenantId, BuiltInRoleKey BuiltInKey,
    PermissionScope PermissionScope, long Version, DateTimeOffset? DeletedAt, UserId? DeletedBy,
    ImmutableArray<PermissionDefinition> Grants, SeedRootEvidence? Evidence = null,
    int AdoptedReleaseVersion = 1, string? AdoptedReleaseFingerprint = null, bool GraphIsValid = false);

public sealed record MembershipSeedDto(Guid Id, TenantId TenantId, OrganizationId? OrganizationId,
    UserId UserId, string Status, long Version, DateTimeOffset? DeletedAt, UserId? DeletedBy,
    [property: PiiSensitive] string? DisplayName, [property: PiiSensitive] string? PreferredLocale,
    ImmutableArray<Guid> RoleIds, SeedRootEvidence? Evidence = null, bool GraphIsValid = false);

public sealed record GetRoleSeedStateQuery(Guid RoleId, BuiltInRoleKey BuiltInKey,
    PermissionScope CapabilityScope, int ManifestVersion, string ExpectedFingerprint)
    : IRequest<Result<SeedLookup<RoleSeedDto>>>;

public sealed record GetMembershipSeedStateQuery(Guid MembershipId, UserId UserId, Guid RoleId,
    PermissionScope MembershipScope, int ManifestVersion, string ExpectedFingerprint)
    : IRequest<Result<SeedLookup<MembershipSeedDto>>>;

using System.Collections.Immutable;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Roles;

public sealed record ListRolesQuery() : IRequest<Result<RoleDirectoryDto>>;
public sealed record GetRoleDetailQuery(Guid RoleId) : IRequest<Result<RoleDetailDto>>;
public sealed record GetPermissionRegistryQuery() : IRequest<Result<PermissionRegistryDto>>;

public sealed record RoleDirectoryItemDto(Guid RoleId, string BuiltInKey, PermissionScope CapabilityScope, long Version);
public sealed record RoleDirectoryDto(ImmutableArray<RoleDirectoryItemDto> Items);
public sealed record RoleDetailDto(Guid RoleId, string BuiltInKey, PermissionScope CapabilityScope, long Version,
    ImmutableArray<PermissionDefinition> Grants, int AdoptedReleaseVersion, string AdoptedReleaseFingerprint);
public sealed record PermissionRegistryItemDto(PermissionScope Scope, string Key, string LabelKey, bool CanGrant);
public sealed record RoleReleaseDto(int Version, string Fingerprint, ImmutableArray<PermissionDefinition> TenantAdminDelta);
public sealed record PermissionRegistryDto(ImmutableArray<PermissionRegistryItemDto> Items, RoleReleaseDto Release);

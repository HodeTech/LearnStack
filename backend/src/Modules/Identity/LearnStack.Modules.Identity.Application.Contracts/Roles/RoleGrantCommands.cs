using System.Collections.Immutable;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Roles;

public sealed record ChangeRoleGrantsCommand(Guid RoleId, long ExpectedRoleVersion, ImmutableArray<PermissionDefinition> Grants)
    : IRequest<Result<long>>, IIdentityPermissionRequest
{
    public PermissionDefinition RequiredPermission => new(PermissionScope.Tenant, "identity.role.admin");
}

public sealed record AdoptRoleGrantManifestCommand(Guid RoleId, long ExpectedRoleVersion, int ManifestVersion, string ManifestFingerprint)
    : IRequest<Result<long>>, IIdentityPermissionRequest
{
    public PermissionDefinition RequiredPermission => new(PermissionScope.Tenant, "identity.role.admin");
}

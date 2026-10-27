using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Contracts.Authorization;

public enum IdentityAuthorizationOutcome { Unavailable = 0, NotCovered = 1, Covered = 2 }

/// <summary>Module-local identifiers cross this contract as Guid; no profile or provider data is projected.</summary>
public sealed record AuthorizationRoleFacts(Guid RoleId, BuiltInRoleKey BuiltInKey,
    PermissionScope Scope, ImmutableArray<PermissionDefinition> Grants);

public sealed record AuthorizationMembershipFacts(Guid MembershipId, OrganizationId? OrganizationId,
    ImmutableArray<AuthorizationRoleFacts> Roles);

/// <summary>Immutable facts from one completed committed-state read, not a reusable permission decision.</summary>
public sealed record IdentityAuthorizationResult(
    IdentityAuthorizationOutcome Outcome,
    UserId Actor,
    TenantId TenantId,
    OrganizationId? OrganizationId,
    ImmutableArray<AuthorizationMembershipFacts> Memberships)
{
    public bool Covers => Outcome == IdentityAuthorizationOutcome.Covered;
}

public interface IIdentityAuthorizationReader
{
    Task<IdentityAuthorizationResult> ReadAsync(AuthorizationCandidate candidate,
        CancellationToken cancellationToken = default);
}

using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>Pure current-self previews. Computing a false flag never attempts a protected operation.</summary>
internal static class OwnCapabilityProjection
{
    internal static OwnCapabilitiesDto Create(IdentityAuthorizationResult facts, IOperationPolicyCatalog catalog)
    {
        var tenantAdmin = Holds(facts, catalog, IdentityOperationPolicySource.MembershipAdministration);
        var memberAdmin = tenantAdmin || Holds(facts, catalog, IdentityOperationPolicySource.OrganizationMembershipAdministration);
        var profile = Holds(facts, catalog, IdentityOperationPolicySource.ProfileWrite)
            || Holds(facts, catalog, IdentityOperationPolicySource.OrganizationProfileWrite);
        var roleRead = Holds(facts, catalog, IdentityOperationPolicySource.RoleAdministration);
        var roleWrite = facts.OrganizationId is null && roleRead && facts.Memberships
            .Where(member => member.OrganizationId is null).SelectMany(member => member.Roles)
            .Any(role => role.BuiltInKey == BuiltInRoleKey.TenantAdmin && role.Scope == PermissionScope.Tenant
                && IdentityFoundationManifest.Version1.TenantAdminKernel.All(definition =>
                    IdentityPolicyEvaluation.IsRegistered(definition, catalog) && role.Grants.Contains(definition)));
        return new(tenantAdmin, facts.OrganizationId is not null && memberAdmin, memberAdmin && profile, memberAdmin,
            roleRead, roleWrite, roleWrite, profile && facts.Memberships.Any(member => member.OrganizationId == facts.OrganizationId),
            facts.OrganizationId is null && Holds(facts, catalog, BoundedTenancyAdministrationPermissions.OrganizationAdministration));
    }

    internal static bool CanGrant(IdentityAuthorizationResult facts, IOperationPolicyCatalog catalog, PermissionDefinition target) =>
        Create(facts, catalog).CanChangeRoleGrants && IdentityPolicyEvaluation.IsRegistered(target, catalog)
        && Holds(facts, catalog, new PermissionDefinition(PermissionScope.Tenant, target.Key));

    private static bool Holds(IdentityAuthorizationResult facts, IOperationPolicyCatalog catalog, PermissionDefinition permission) =>
        IdentityPolicyEvaluation.IsRegistered(permission, catalog) && facts.Memberships
            .Where(member => permission.Scope == PermissionScope.Tenant ? member.OrganizationId is null
                : facts.OrganizationId is not null && member.OrganizationId == facts.OrganizationId)
            .SelectMany(member => member.Roles).Any(role => role.Scope == permission.Scope && role.Grants.Contains(permission));
}

using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class RoleGrantCommandIntegrationTests
{
    [Theory]
    [InlineData(PermissionScope.Tenant, "identity.membership.admin")]
    [InlineData(PermissionScope.Organization, "identity.membership.admin")]
    [InlineData(PermissionScope.Tenant, "identity.role.admin")]
    [InlineData(PermissionScope.Tenant, "identity.membership_profile.write")]
    [InlineData(PermissionScope.Organization, "identity.membership_profile.write")]
    public async Task Every_assignable_pair_requires_a_legal_delegation_target_with_committed_denial_and_no_refused_mutation(
        PermissionScope permissionScope, string permissionKey)
    {
        var roleAdministration = permissionKey == "identity.role.admin";
        var matchingKind = roleAdministration ? BuiltInRoleKey.TenantAdmin
            : permissionScope == PermissionScope.Tenant ? BuiltInRoleKey.Instructor : BuiltInRoleKey.OrgInstructor;
        var oppositeScope = roleAdministration ? PermissionScope.Tenant
            : permissionScope == PermissionScope.Tenant ? PermissionScope.Organization : PermissionScope.Tenant;
        var oppositeKind = oppositeScope == PermissionScope.Tenant ? BuiltInRoleKey.Instructor : BuiltInRoleKey.OrgInstructor;
        var matchingRole = RoleId(SchemaFixture.TenantA, matchingKind, permissionScope);
        var oppositeRole = RoleId(SchemaFixture.TenantA, oppositeKind, oppositeScope);
        var pair = new PermissionDefinition(permissionScope, permissionKey);
        // Role administration is confined to TenantAdmin and its inseparable kernel.
        // Its positive mutation removes the optional profile grant while retaining that kernel.
        ImmutableArray<PermissionDefinition> desired = roleAdministration ? IdentityFoundationManifest.Version1.TenantAdminKernel : [pair];
        var correlation = "registry-delegation-" + Guid.NewGuid().ToString("N");
        try
        {
            var matchingBefore = await ReadRoleAsync(matchingRole);
            var positive = await SendAsync(new ChangeRoleGrantsCommand(matchingRole, matchingBefore.Version, desired), correlation + "-positive");
            Assert.True(positive.IsSuccess);
            var matchingAfter = await ReadRoleAsync(matchingRole);
            Assert.Equal(matchingBefore.Version + 1, matchingAfter.Version);
            Assert.Equal(desired.Select(definition => definition.Scope + ":" + definition.Key).Order(StringComparer.Ordinal), matchingAfter.Grants);
            Assert.Equal("success", (await AuditAsync(correlation + "-positive", "identity.role.change_grants")).Outcome);

            // This checks actual target-pair delegation, including the RoleAdmin template restriction.
            // It does not invent an underprivileged administrator or a future profile editing route.
            var before = await ReadRoleAsync(oppositeRole);
            var refused = await SendAsync(new ChangeRoleGrantsCommand(oppositeRole, before.Version, [pair]), correlation);
            Assert.Equal("forbidden", refused.Error!.Code);
            var after = await ReadRoleAsync(oppositeRole);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.Grants, after.Grants);
            var denial = await AuditAsync(correlation, "identity.authorization.deny");
            Assert.Equal("denied", denial.Outcome);
            using var metadata = System.Text.Json.JsonDocument.Parse(denial.Metadata);
            Assert.Equal("delegation", metadata.RootElement.GetProperty("denial_reason").GetString());
        }
        finally
        {
            await RestoreAsync(matchingKind, permissionScope);
            await RestoreAsync(oppositeKind, oppositeScope);
        }
    }
}

using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class RoleGrantDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());

    [Fact]
    public void Replacement_retains_unchanged_children_and_versions_only_a_real_difference()
    {
        var role = Create(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var retained = role.Grants.Where(grant => grant.PermissionKey != IdentityFoundationManifest.ProfileWrite).ToArray();
        var kernel = IdentityFoundationManifest.Version1.TenantAdminKernel;
        Assert.True(role.ReplaceGrants(kernel, At.AddSeconds(1), Actor).IsSuccess);
        Assert.Equal(1, role.Version);
        Assert.Equal(At.AddSeconds(1), role.UpdatedAt);
        Assert.Equal(Actor, role.UpdatedBy);
        Assert.All(retained, grant => Assert.Contains(role.Grants, current => ReferenceEquals(grant, current)));
        Assert.True(role.ReplaceGrants(kernel.Reverse().ToArray(), At.AddSeconds(2), Actor).IsSuccess);
        Assert.Equal(1, role.Version);
        var initial = IdentityFoundationManifest.Version1.FindTemplate(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)!.Grants;
        Assert.True(role.ReplaceGrants(initial, At.AddSeconds(3), Actor).IsSuccess);
        Assert.Equal(2, role.Version);
        Assert.All(retained, grant => Assert.Contains(role.Grants, current => ReferenceEquals(grant, current)));
        Assert.All(role.Grants, grant => { Assert.Same(role, grant.Role); Assert.Equal(role.Id, grant.RoleId); Assert.Equal(Tenant, grant.TenantId); });
    }

    [Theory]
    [InlineData("kernel")]
    [InlineData("scope")]
    [InlineData("unknown")]
    [InlineData("virtual")]
    [InlineData("inactive")]
    [InlineData("duplicate")]
    public void Invalid_replacement_never_changes_the_existing_graph(string variant)
    {
        var role = Create(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var original = role.Grants.ToArray();
        var desired = variant switch
        {
            "kernel" => new[] { new PermissionDefinition(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin) },
            "scope" => [new(PermissionScope.Organization, IdentityFoundationManifest.MembershipAdmin)],
            "unknown" => [new(PermissionScope.Tenant, "identity.unknown.write")],
            "virtual" => [new(PermissionScope.Tenant, "education.public_course.read")],
            "inactive" => [new(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin, isActive: false)],
            _ => [new(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin), new(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin)],
        };
        Assert.False(role.CanReplaceGrants(desired));
        Assert.True(role.ReplaceGrants(desired, At.AddSeconds(1), Actor).IsFailure);
        Assert.Equal(0, role.Version);
        Assert.Equal(original, role.Grants);
        Assert.Null(role.UpdatedAt);
    }

    [Fact]
    public void Role_admin_cannot_be_delegated_to_another_template_and_kernel_is_independent_of_other_administrators()
    {
        var instructor = Create(BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        Assert.True(instructor.ReplaceGrants([new(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin)], At, Actor).IsFailure);
        var first = Create(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var second = Create(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        Assert.True(second.IsTenantAdministrator);
        Assert.True(first.ReplaceGrants([], At, Actor).IsFailure);
        Assert.True(first.IsTenantAdministrator);
    }

    private static Role Create(BuiltInRoleKey key, PermissionScope scope) =>
        Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, key, scope, At, Actor).Value!;
}

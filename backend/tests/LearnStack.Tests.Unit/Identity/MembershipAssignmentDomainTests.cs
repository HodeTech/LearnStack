using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class MembershipAssignmentDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly UserId Editor = UserId.From(Guid.CreateVersion7());

    [Fact]
    public void Replacement_changes_only_actual_children_and_retains_unchanged_instances()
    {
        var learner = NewRole(BuiltInRoleKey.Learner);
        var instructor = NewRole(BuiltInRoleKey.Instructor);
        var guardian = NewRole(BuiltInRoleKey.Guardian);
        var membership = NewMembership(learner);
        var profile = membership.Profile;
        var retained = Assert.Single(membership.RoleAssignments);
        Assert.True(membership.ReplaceAssignments([learner, instructor], At.AddSeconds(1), Actor).IsSuccess);
        var removed = Assert.Single(membership.RoleAssignments, assignment => assignment.RoleId == instructor.Id);

        Assert.True(membership.ReplaceAssignments([guardian, learner], At.AddSeconds(2), Editor).IsSuccess);

        Assert.Equal(2, membership.Version);
        Assert.Equal(At.AddSeconds(2), membership.UpdatedAt);
        Assert.Equal(Editor, membership.UpdatedBy);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Same(profile, membership.Profile);
        Assert.Equal(2, membership.RoleAssignments.Count);
        Assert.Same(retained, Assert.Single(membership.RoleAssignments, assignment => assignment.RoleId == learner.Id));
        Assert.DoesNotContain(removed, membership.RoleAssignments);
        Assert.Contains(membership.RoleAssignments, assignment => assignment.RoleId == guardian.Id);
        Assert.All(membership.RoleAssignments, assignment =>
        {
            Assert.Same(membership, assignment.Membership);
            Assert.Equal(membership.Id, assignment.MembershipId);
            Assert.Equal(Tenant, assignment.TenantId);
            Assert.Null(assignment.OrganizationId);
        });
        Assert.Equal(0, learner.Version);
        Assert.Equal(0, instructor.Version);
        Assert.Equal(0, guardian.Version);
    }

    [Fact]
    public void Identical_sets_ignore_order_without_stamping_or_replacing_children()
    {
        var learner = NewRole(BuiltInRoleKey.Learner);
        var instructor = NewRole(BuiltInRoleKey.Instructor);
        var membership = NewMembership(learner);
        var original = Assert.Single(membership.RoleAssignments);

        Assert.True(membership.ReplaceAssignments([learner], At.AddSeconds(1), Editor).IsSuccess);
        Assert.Equal(0, membership.Version);
        Assert.Null(membership.UpdatedAt);
        Assert.Null(membership.UpdatedBy);
        Assert.Same(original, Assert.Single(membership.RoleAssignments));

        Assert.True(membership.ReplaceAssignments([learner, instructor], At.AddSeconds(2), Actor).IsSuccess);
        var children = membership.RoleAssignments.ToArray();
        Assert.True(membership.ReplaceAssignments([instructor, learner], At.AddSeconds(3), Editor).IsSuccess);
        Assert.Equal(1, membership.Version);
        Assert.Equal(At.AddSeconds(2), membership.UpdatedAt);
        Assert.Equal(Actor, membership.UpdatedBy);
        Assert.Equal(children, membership.RoleAssignments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compatible_assignments_derive_the_exact_membership_scope(bool organizationScoped)
    {
        var scope = organizationScoped ? PermissionScope.Organization : PermissionScope.Tenant;
        var organization = organizationScoped ? OrganizationId.From(Guid.CreateVersion7()) : (OrganizationId?)null;
        var learner = NewRole(BuiltInRoleKey.Learner, scope);
        var instructor = NewRole(organizationScoped ? BuiltInRoleKey.OrgInstructor : BuiltInRoleKey.Instructor, scope);
        var membership = NewMembership(learner, organization);

        Assert.True(membership.ReplaceAssignments([learner, instructor], At.AddSeconds(1), Editor).IsSuccess);

        Assert.Equal(2, membership.RoleAssignments.Count);
        Assert.All(membership.RoleAssignments, assignment =>
        {
            Assert.Equal(Tenant, assignment.TenantId);
            Assert.Equal(organization, assignment.OrganizationId);
            Assert.Equal(membership.Id, assignment.MembershipId);
            Assert.Same(membership, assignment.Membership);
        });
    }

    [Fact]
    public void Suspended_membership_can_prepare_assignments_without_reactivation_or_profile_changes()
    {
        var learner = NewRole(BuiltInRoleKey.Learner);
        var instructor = NewRole(BuiltInRoleKey.Instructor);
        var membership = NewMembership(learner);
        Assert.True(membership.SetProfile("Contextual name", "en", At.AddSeconds(1), Actor).IsSuccess);
        Assert.True(membership.Suspend(At.AddSeconds(2), Actor).IsSuccess);

        Assert.True(membership.ReplaceAssignments([instructor], At.AddSeconds(3), Editor).IsSuccess);

        Assert.Equal(MembershipStatus.Suspended, membership.Status);
        Assert.False(membership.IsDeleted);
        Assert.Equal(3, membership.Version);
        Assert.Equal(instructor.Id, Assert.Single(membership.RoleAssignments).RoleId);
        Assert.Equal("Contextual name", membership.Profile.DisplayName);
        Assert.Equal("en", membership.Profile.PreferredLocale);
        Assert.True(membership.SetProfile("Changed", "tr", At.AddSeconds(4), Editor).IsFailure);
        Assert.Equal(3, membership.Version);
    }

    [Fact]
    public void Removing_all_assignments_is_a_root_change_and_repeating_it_is_a_noop()
    {
        var membership = NewMembership(NewRole(BuiltInRoleKey.Learner));

        Assert.True(membership.ReplaceAssignments([], At.AddSeconds(1), Actor).IsSuccess);
        Assert.Empty(membership.RoleAssignments);
        Assert.Equal(1, membership.Version);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.True(membership.ReplaceAssignments([], At.AddSeconds(2), Editor).IsSuccess);
        Assert.Equal(1, membership.Version);
        Assert.Equal(At.AddSeconds(1), membership.UpdatedAt);
        Assert.Equal(Actor, membership.UpdatedBy);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("scope")]
    [InlineData("deleted")]
    [InlineData("duplicate")]
    [InlineData("same-id")]
    [InlineData("null")]
    [InlineData("too-many")]
    public void Invalid_role_sets_preserve_the_complete_existing_graph(string variant)
    {
        var learner = NewRole(BuiltInRoleKey.Learner);
        var membership = NewMembership(learner);
        var original = Assert.Single(membership.RoleAssignments);
        Role[] desired;
        switch (variant)
        {
            case "foreign":
                desired = [learner, NewRole(BuiltInRoleKey.Learner, tenant: TenantId.From(Guid.CreateVersion7()))];
                break;
            case "scope":
                desired = [learner, NewRole(BuiltInRoleKey.Learner, PermissionScope.Organization)];
                break;
            case "deleted":
                var deleted = NewRole(BuiltInRoleKey.Instructor);
                deleted.SoftDelete(At.AddSeconds(1), Actor);
                desired = [learner, deleted];
                break;
            case "duplicate":
                desired = [learner, learner];
                break;
            case "same-id":
                var sameId = Role.Create(learner.Id, Tenant, BuiltInRoleKey.Learner, PermissionScope.Tenant, At, Actor).Value!;
                desired = [learner, sameId];
                break;
            case "null":
                desired = [learner, null!];
                break;
            default:
                desired = Enumerable.Range(0, 9).Select(_ => NewRole(BuiltInRoleKey.Learner)).ToArray();
                break;
        }

        Assert.True(membership.ReplaceAssignments(desired, At.AddSeconds(2), Editor).IsFailure);

        Assert.Same(original, Assert.Single(membership.RoleAssignments));
        Assert.Equal(learner.Id, original.RoleId);
        Assert.Equal(0, membership.Version);
        Assert.Null(membership.UpdatedAt);
        Assert.Null(membership.UpdatedBy);
        Assert.Equal(MembershipStatus.Active, membership.Status);
    }

    [Fact]
    public void Organization_membership_rejects_a_tenant_capability_role_without_scope_retargeting()
    {
        var organization = OrganizationId.From(Guid.CreateVersion7());
        var learner = NewRole(BuiltInRoleKey.Learner, PermissionScope.Organization);
        var membership = NewMembership(learner, organization);
        var original = Assert.Single(membership.RoleAssignments);

        Assert.True(membership.ReplaceAssignments([NewRole(BuiltInRoleKey.TenantAdmin)], At.AddSeconds(1), Editor).IsFailure);

        Assert.Same(original, Assert.Single(membership.RoleAssignments));
        Assert.Equal(organization, membership.OrganizationId);
        Assert.Equal(organization, original.OrganizationId);
        Assert.Equal(0, membership.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Revoked_or_deleted_memberships_refuse_even_an_identical_or_empty_assignment_set(bool revoke)
    {
        var learner = NewRole(BuiltInRoleKey.Learner);
        var membership = NewMembership(learner);
        var original = Assert.Single(membership.RoleAssignments);
        if (revoke) Assert.True(membership.Revoke(At.AddSeconds(1), Actor).IsSuccess);
        else membership.SoftDelete(At.AddSeconds(1), Actor);

        Assert.True(membership.ReplaceAssignments([learner], At.AddSeconds(2), Editor).IsFailure);
        Assert.True(membership.ReplaceAssignments([], At.AddSeconds(2), Editor).IsFailure);

        Assert.Same(original, Assert.Single(membership.RoleAssignments));
        Assert.Equal(1, membership.Version);
        Assert.Equal(At.AddSeconds(1), membership.UpdatedAt);
        Assert.Equal(Actor, membership.UpdatedBy);
        Assert.Equal(At.AddSeconds(1), membership.DeletedAt);
        Assert.Equal(Actor, membership.DeletedBy);
        Assert.Equal(revoke ? MembershipStatus.Revoked : MembershipStatus.Active, membership.Status);
    }

    private static Role NewRole(BuiltInRoleKey key, PermissionScope scope = PermissionScope.Tenant, TenantId? tenant = null) =>
        Role.Create(RoleId.From(Guid.CreateVersion7()), tenant ?? Tenant, key, scope, At, Actor).Value!;

    private static Membership NewMembership(Role role, OrganizationId? organization = null) => Membership.Create(
        MembershipId.From(Guid.CreateVersion7()), Tenant, organization, Actor, role, At, Actor).Value!;
}

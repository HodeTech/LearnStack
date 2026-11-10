using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.CustomFields;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class CustomFieldConsentMembershipReaderTests
{
    [Fact]
    public async Task Grant_projection_derives_subject_and_exact_membership_and_rechecks_lifecycle()
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        var actor = UserId.From(Guid.CreateVersion7());
        var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var role = Role.Create(RoleId.From(Guid.CreateVersion7()), tenant, BuiltInRoleKey.Learner, PermissionScope.Tenant, at, actor).Value!;
        var member = Membership.Create(MembershipId.From(Guid.CreateVersion7()), tenant, null, actor, role, at, actor).Value!;
        var definition = Guid.CreateVersion7();
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(true);
        context.TenantId.Returns(tenant);
        context.UserId.Returns(actor);
        var memberships = Substitute.For<IMembershipAdministrationReader>();
        memberships.ReadOwnAsync(Arg.Any<CancellationToken>()).Returns(member);
        var lifecycle = Substitute.For<IIdentityScopeLifecycleReader>();
        lifecycle.ReadAsync(tenant, null, Arg.Any<CancellationToken>()).Returns(new IdentityScopeLifecycle(true, true, false, false));
        var locks = Substitute.For<IIdentityMutationLocks>();
        var fence = new IdentityMutationLockPlan([], tenant, IdentityTenantLockMode.Exclusive);
        var reader = new CustomFieldConsentMembershipReader(memberships, lifecycle, context, locks);
        var result = await reader.ReadOwnActiveAsync(member.Id.Value, definition, 1, fence);
        Assert.NotNull(result);
        Assert.Equal(tenant, result.TenantId);
        Assert.Equal(actor, result.SubjectUserId);
        Assert.Equal(member.Id.Value, result.MembershipId);
        Assert.Null(result.OrganizationId);
        Assert.False(result.HasExactRetainedValuePin);
        locks.Received(2).RequireCurrent(Arg.Is<IdentityMutationLockPlan>(plan => ReferenceEquals(plan, fence)));
        Assert.Null(await reader.ReadOwnActiveAsync(Guid.CreateVersion7(), definition, 1, fence));
        lifecycle.ReadAsync(tenant, null, Arg.Any<CancellationToken>()).Returns(new IdentityScopeLifecycle(true, false, false, false));
        Assert.Null(await reader.ReadOwnActiveAsync(member.Id.Value, definition, 1, fence));
        lifecycle.ReadAsync(tenant, null, Arg.Any<CancellationToken>()).Returns(new IdentityScopeLifecycle(true, true, false, false));
        Assert.True(member.Suspend(at, actor).IsSuccess);
        Assert.Null(await reader.ReadOwnActiveAsync(member.Id.Value, definition, 1, fence));
    }

    [Fact]
    public async Task Shared_or_foreign_fence_never_admits_consent_membership_projection()
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        var context = Substitute.For<ITenantContext>();
        context.TenantId.Returns(tenant);
        var memberships = Substitute.For<IMembershipAdministrationReader>();
        var reader = new CustomFieldConsentMembershipReader(memberships, Substitute.For<IIdentityScopeLifecycleReader>(), context,
            Substitute.For<IIdentityMutationLocks>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadOwnActiveAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), 1,
            new([], tenant, IdentityTenantLockMode.Shared)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadOwnActiveAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), 1,
            new([], TenantId.From(Guid.CreateVersion7()), IdentityTenantLockMode.Exclusive)));
        await memberships.DidNotReceive().ReadOwnAsync(Arg.Any<CancellationToken>());
    }
}

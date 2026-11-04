using FluentAssertions;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class SeedIdentityFreshContinuationTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01916d82-4b10-7000-8000-000000000001"));
    private static readonly UserId User = UserId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a001"));
    private static readonly MembershipId Member = MembershipId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a201"));
    private static readonly RoleId RoleId = RoleId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a101"));
    private static readonly DateTimeOffset At = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Role_grant_removal_while_seed_waits_refuses_the_remaining_profile_act()
    {
        var fixture = new Fixture();
        var editedRole = NewRole();
        editedRole.ReplaceGrants([], At.AddMinutes(1), User).IsSuccess.Should().BeTrue();
        fixture.Roles.ReadAsync(RoleId, Arg.Any<CancellationToken>()).Returns(NewRole(), editedRole);
        var result = await fixture.Handler().Handle(Command(), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        await fixture.Store.Received(1).LockAsync(Member, Arg.Any<CancellationToken>());
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        fixture.Membership.Version.Should().Be(0);
    }

    [Fact]
    public async Task Tenant_locale_edit_observed_under_the_fence_refuses_the_remaining_profile_act()
    {
        var fixture = new Fixture();
        fixture.Continuation.VerifyAsync(Arg.Any<object>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(false);
        (await fixture.Handler().Handle(Command(), CancellationToken.None)).IsFailure.Should().BeTrue();
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan =>
            plan.TenantMode == IdentityTenantLockMode.Exclusive && plan.UserIds.SequenceEqual(new[] { User })), Arg.Any<CancellationToken>());
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Fact]
    public async Task Whole_prefix_refusal_after_the_complete_plan_and_retained_root_lock_prevents_profile_initialization()
    {
        var fixture = new Fixture();
        var request = Command();
        IdentityMutationLockPlan? held = null;
        var rootLocked = false;
        fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            held = call.Arg<IdentityMutationLockPlan>();
            return Task.CompletedTask;
        });
        fixture.Store.LockAsync(Member, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            rootLocked = true;
            return Task.CompletedTask;
        });
        fixture.Continuation.VerifyAsync(Arg.Any<object>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Same(request, call[0]);
                Assert.Same(held, call.Arg<IdentityMutationLockPlan>());
                Assert.True(rootLocked);
                return false;
            });
        (await fixture.Handler().Handle(request, default)).IsFailure.Should().BeTrue();
        await fixture.Locks.Received(1).AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        fixture.Membership.Version.Should().Be(0);
        fixture.Membership.Profile.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task Edited_empty_profile_after_wait_is_preserved_instead_of_initialized_again()
    {
        var fixture = new Fixture();
        var edited = Membership.Create(Member, Tenant, null, User, NewRole(), At, UserId.SystemActor).Value!;
        edited.SetProfile("Human edit", "en", At.AddMinutes(1), User).IsSuccess.Should().BeTrue();
        edited.SetProfile(null, null, At.AddMinutes(2), User).IsSuccess.Should().BeTrue();
        fixture.Store.FindAsync(Member, Arg.Any<CancellationToken>()).Returns(fixture.Membership, edited);
        (await fixture.Handler().Handle(Command(), CancellationToken.None)).IsFailure.Should().BeTrue();
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        edited.Version.Should().Be(2);
        edited.Profile.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task Closed_verification_can_read_retained_inactive_history_without_lifecycle_write_admission()
    {
        var fixture = new Fixture();
        var reader = Substitute.For<ISeedStateReader>();
        var row = new MembershipSeedDto(Member.Value, Tenant, null, User, "Revoked", 2, At, User,
            null, null, [], new(At, UserId.SystemActor, At, User), GraphIsValid: true);
        reader.ReadMembershipAsync(Member, Arg.Any<CancellationToken>()).Returns(row);
        var handler = new GetMembershipSeedStateQueryHandler(reader, fixture.Context, fixture.Admission, fixture.Lifecycle);
        var result = await handler.Handle(new(Member.Value, User, RoleId.Value, PermissionScope.Tenant, 1,
            IdentityFoundationManifest.Version1.MembershipFingerprint(Tenant, null, Member.Value, User, RoleId.Value)), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value!.State.Should().BeSameAs(row);
        await fixture.Lifecycle.DidNotReceiveWithAnyArgs().ReadAsync(Tenant, null);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
    }

    private static Role NewRole() => Role.Create(RoleId, Tenant, BuiltInRoleKey.Learner, PermissionScope.Tenant, At, UserId.SystemActor).Value!;
    private static SetSeedMembershipProfileCommand Command() => new(Member.Value, User, RoleId.Value, PermissionScope.Tenant,
        "Demo", "en", 1, IdentityFoundationManifest.Version1.ProfileFingerprint(Tenant, null, Member.Value, "Demo", "en"));

    private sealed class Fixture
    {
        internal Fixture()
        {
            Context.IsResolved.Returns(true);
            Context.TenantId.Returns(Tenant);
            Context.UserId.Returns(UserId.SystemActor);
#pragma warning disable CA2012 // NSubstitute configuration; each execution receives a fresh ValueTask.
            Admission.AllowsFoundationAsync(Arg.Any<SeedIdentityFoundationAct>(), Arg.Any<CancellationToken>())
                .Returns(_ => ValueTask.FromResult(true));
#pragma warning restore CA2012
            Lifecycle.ReadAsync(Tenant, null, Arg.Any<CancellationToken>()).Returns(new IdentityScopeLifecycle(true, true, false, false));
            Continuation.VerifyAsync(Arg.Any<object>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(true);
            Roles.ReadAsync(RoleId, Arg.Any<CancellationToken>()).Returns(NewRole());
            Membership = LearnStack.Modules.Identity.Domain.Membership.Create(Member, Tenant, null, User, NewRole(), At, UserId.SystemActor).Value!;
            Store.FindAsync(Member, Arg.Any<CancellationToken>()).Returns(Membership);
            Clock.UtcNow.Returns(At.AddMinutes(3));
        }
        internal Membership Membership { get; }
        internal IMembershipWriteStore Store { get; } = Substitute.For<IMembershipWriteStore>();
        internal IRoleReadStore Roles { get; } = Substitute.For<IRoleReadStore>();
        internal ITenantContext Context { get; } = Substitute.For<ITenantContext>();
        internal ISeedIdentityFoundationAdmission Admission { get; } = Substitute.For<ISeedIdentityFoundationAdmission>();
        internal IIdentityScopeLifecycleReader Lifecycle { get; } = Substitute.For<IIdentityScopeLifecycleReader>();
        internal IIdentityMutationLocks Locks { get; } = Substitute.For<IIdentityMutationLocks>();
        internal IFixedSeedPrerequisiteVerifier Continuation { get; } = Substitute.For<IFixedSeedPrerequisiteVerifier>();
        internal IClock Clock { get; } = Substitute.For<IClock>();
        internal SetSeedMembershipProfileCommandHandler Handler() => new(Store, Context, Admission, Lifecycle, Locks, Clock, Roles, Continuation);
    }
}

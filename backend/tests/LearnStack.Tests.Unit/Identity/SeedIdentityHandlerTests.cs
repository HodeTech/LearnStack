using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using NSubstitute;
using NSubstitute.Extensions;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class SeedIdentityHandlerTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01916d82-4b10-7000-8000-000000000001"));
    private static readonly UserId User = UserId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a001"));
    private static readonly Guid Member = Guid.Parse("01916d82-4b10-7000-8000-00000000a201");
    private static readonly Guid RoleGuid = Guid.Parse("01916d82-4b10-7000-8000-00000000a101");
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task System_actor_and_valid_manifest_without_closed_admission_touch_no_sql_or_locks()
    {
        var fixture = new Fixture(false);
        var result = await fixture.FirstAdmin().Handle(FirstAdminCommand(), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        await fixture.Lifecycle.DidNotReceiveWithAnyArgs().ReadAsync(Tenant, null);
        await fixture.Roles.DidNotReceiveWithAnyArgs().ReadAsync(RoleId.From(RoleGuid));
        await fixture.Memberships.DidNotReceiveWithAnyArgs().FindAsync(MembershipId.From(Member));
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
    }

    [Fact]
    public async Task First_admin_preholds_the_actual_user_and_exclusive_tenant_before_writing_one_complete_root()
    {
        var fixture = new Fixture();
        fixture.Roles.ReadAsync(RoleId.From(RoleGuid), Arg.Any<CancellationToken>()).Returns(RoleFor(BuiltInRoleKey.TenantAdmin));
        var events = new List<string>();
        fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("lock"); return Task.CompletedTask; });
        fixture.Locks.When(locks => locks.RequireCurrent(Arg.Any<IdentityMutationLockPlan>()))
            .Do(_ => events.Add("receipt"));
        fixture.Memberships.AddAsync(Arg.Any<Membership>(), Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("write"); return Task.CompletedTask; });
        fixture.Memberships.ValidateGraphAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("graph"); return Task.CompletedTask; });
        var result = await fixture.FirstAdmin().Handle(FirstAdminCommand(), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        events.Should().Equal("lock", "receipt", "write", "graph");
        await fixture.Roles.Received(2).ReadAsync(RoleId.From(RoleGuid), Arg.Any<CancellationToken>());
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan =>
            plan.UserIds.Length == 1 && plan.UserIds[0] == User && plan.TenantMode == IdentityTenantLockMode.Exclusive),
            Arg.Any<CancellationToken>());
        await fixture.Memberships.Received(1).AddAsync(Arg.Is<Membership>(member => member.Id.Value == Member
            && member.Profile.MembershipId == member.Id && member.RoleAssignments.Count == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Changed_postwait_role_classification_refuses_without_upgrade_or_mutation()
    {
        var fixture = new Fixture();
        fixture.Roles.ReadAsync(RoleId.From(RoleGuid), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Role?>(RoleFor(BuiltInRoleKey.TenantAdmin)),
                Task.FromResult<Role?>(RoleFor(BuiltInRoleKey.Instructor)));
        var result = await fixture.FirstAdmin().Handle(FirstAdminCommand(), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        await fixture.Locks.Received(1).AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
        await fixture.Memberships.DidNotReceiveWithAnyArgs().AddAsync(null!);
        fixture.Locks.DidNotReceiveWithAnyArgs().RequireCurrent(null!);
    }

    [Fact]
    public async Task Learner_plan_is_shared_and_still_fences_its_actual_target_user()
    {
        var fixture = new Fixture();
        fixture.Roles.ReadAsync(RoleId.From(RoleGuid), Arg.Any<CancellationToken>()).Returns(RoleFor(BuiltInRoleKey.Learner));
        var handler = new CreateSeedMembershipCommandHandler(fixture.Memberships, fixture.Roles, fixture.Context,
            fixture.Admission, fixture.Lifecycle, fixture.Locks, fixture.Clock);
        var result = await handler.Handle(new CreateSeedMembershipCommand(Member, User, RoleGuid, PermissionScope.Tenant,
            1, MembershipFingerprint()), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan =>
            plan.UserIds.Length == 1 && plan.UserIds[0] == User && plan.TenantMode == IdentityTenantLockMode.Shared),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Profile_change_uses_shared_tenant_without_a_user_fence_and_cannot_repair_an_edited_profile()
    {
        var fixture = new Fixture();
        var member = Membership.Create(MembershipId.From(Member), Tenant, null, User,
            RoleFor(BuiltInRoleKey.Learner), At, User).Value!;
        fixture.Memberships.FindAsync(MembershipId.From(Member), Arg.Any<CancellationToken>()).Returns(member);
        var handler = new SetSeedMembershipProfileCommandHandler(fixture.Memberships, fixture.Context,
            fixture.Admission, fixture.Lifecycle, fixture.Locks, fixture.Clock);
        var command = new SetSeedMembershipProfileCommand(Member, PermissionScope.Tenant, "Contextual Name", "en", 1,
            IdentityFoundationManifest.Version1.ProfileFingerprint(Tenant, null, Member, "Contextual Name", "en"));
        var result = await handler.Handle(command, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan =>
            plan.UserIds.IsEmpty && plan.TenantMode == IdentityTenantLockMode.Shared), Arg.Any<CancellationToken>());
        member.Profile.DisplayName.Should().Be("Contextual Name");
        member.SetProfile("Operator edit", "en", At.AddMinutes(2), User).IsSuccess.Should().BeTrue();
        fixture.Locks.ClearReceivedCalls();
        (await handler.Handle(command, CancellationToken.None)).IsFailure.Should().BeTrue();
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        member.Profile.DisplayName.Should().Be("Operator edit");
    }

    [Fact]
    public async Task Verification_query_is_also_closed_before_any_reader_or_lifecycle_sql()
    {
        var fixture = new Fixture(false);
        var reader = Substitute.For<ISeedStateReader>();
        var handler = new GetMembershipSeedStateQueryHandler(reader, fixture.Context, fixture.Admission, fixture.Lifecycle);
        var result = await handler.Handle(new(Member, User, RoleGuid, PermissionScope.Tenant, 1, MembershipFingerprint()),
            CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        await reader.DidNotReceiveWithAnyArgs().ReadMembershipAsync(MembershipId.From(Member));
        await fixture.Lifecycle.DidNotReceiveWithAnyArgs().ReadAsync(Tenant, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_fault_and_nonadmitting_target_return_the_same_fixed_outward_refusal(bool planFault)
    {
        var fixture = new Fixture();
        fixture.Roles.ReadAsync(RoleId.From(RoleGuid), Arg.Any<CancellationToken>()).Returns(RoleFor(BuiltInRoleKey.TenantAdmin));
        if (planFault)
            fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new IdentityMutationLockException()));
        else
            fixture.Memberships.AddAsync(Arg.Any<Membership>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new IdentityFoundationStorageException(IdentityFoundationStorageFailure.Target)));
        var result = await fixture.FirstAdmin().Handle(FirstAdminCommand(), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("identity_foundation_refused");
        result.Error.Details.Should().BeNull();
    }

    [Fact]
    public void Actual_commands_declare_the_five_scoped_definitions_and_exact_audit_subjects()
    {
        var profileTenant = new SetSeedMembershipProfileCommand(Member, PermissionScope.Tenant, null, null, 1, "");
        var profileOrg = profileTenant with { MembershipScope = PermissionScope.Organization };
        IIdentityPermissionRequest[] commands = [FirstAdminCommand(),
            new CreateSeedMembershipCommand(Member, User, RoleGuid, PermissionScope.Organization, 1, ""),
            new InitializeSeedRoleCommand(RoleGuid, BuiltInRoleKey.OrgAdmin, PermissionScope.Organization, 1, ""),
            profileTenant, profileOrg];
        commands.Select(command => command.RequiredPermission).Should().BeEquivalentTo(IdentityFoundationManifest.Version1.Definitions);
        var catalog = new AuditCatalog([new IdentityAuditCatalogSource()]);
        foreach (var command in commands)
        {
            catalog.TryGet(command.GetType(), out var registration).Should().BeTrue();
            registration.Entries.Should().ContainSingle().Which.OperationClass.Should().Be(OperationClass.Must);
            registration.Entries.Single().EntityType.Should().Be(command is InitializeSeedRoleCommand ? typeof(Role) : typeof(Membership));
        }
        catalog.TryGet(typeof(GetRoleSeedStateQuery), out var roleQuery).Should().BeTrue();
        roleQuery.WritesNoRow.Should().BeTrue();
        catalog.TryGet(typeof(GetMembershipSeedStateQuery), out var memberQuery).Should().BeTrue();
        memberQuery.WritesNoRow.Should().BeTrue();
    }

    private static string MembershipFingerprint() => IdentityFoundationManifest.Version1.MembershipFingerprint(
        Tenant, null, Member, User, RoleGuid);
    private static CreateFirstSeedAdminMembershipCommand FirstAdminCommand() => new(Member, User, RoleGuid, 1, MembershipFingerprint());
    private static Role RoleFor(BuiltInRoleKey key) => Role.Create(RoleId.From(RoleGuid), Tenant, key,
        PermissionScope.Tenant, At, User).Value!;

    private sealed class Fixture
    {
        internal Fixture(bool admitted = true)
        {
            Context.IsResolved.Returns(true);
            Context.TenantId.Returns(Tenant);
            Context.UserId.Returns(UserId.SystemActor);
            Admission.ReturnsForAll<ValueTask<bool>>(_ => new ValueTask<bool>(admitted));
            Lifecycle.ReadAsync(Tenant, null, Arg.Any<CancellationToken>())
                .Returns(new IdentityScopeLifecycle(true, true, false, false));
            Clock.UtcNow.Returns(At.AddMinutes(1));
        }
        internal IMembershipWriteStore Memberships { get; } = Substitute.For<IMembershipWriteStore>();
        internal IRoleReadStore Roles { get; } = Substitute.For<IRoleReadStore>();
        internal ITenantContext Context { get; } = Substitute.For<ITenantContext>();
        internal ISeedIdentityFoundationAdmission Admission { get; } = Substitute.For<ISeedIdentityFoundationAdmission>();
        internal IIdentityScopeLifecycleReader Lifecycle { get; } = Substitute.For<IIdentityScopeLifecycleReader>();
        internal IIdentityMutationLocks Locks { get; } = Substitute.For<IIdentityMutationLocks>();
        internal IClock Clock { get; } = Substitute.For<IClock>();
        internal CreateFirstSeedAdminMembershipCommandHandler FirstAdmin() => new(Memberships, Roles, Context, Admission,
            Lifecycle, Locks, Clock);
    }
}

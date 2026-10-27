using System.Collections.Immutable;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class RoleGrantCommandTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid AdminRole = Guid.CreateVersion7();
    private static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Eligible_admin_authors_Organization_pairs_from_exact_same_key_Tenant_ceiling_after_the_lock_wait()
    {
        using var fixture = new Fixture(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization);
        var desired = ImmutableArray.Create(new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.MembershipAdmin));
        var request = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 0, desired);
        fixture.Enter(request);
        var order = new List<string>();
        fixture.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("fresh-authority");
            return fixture.Facts();
        });
        fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("lock"); return Task.CompletedTask; });
        fixture.Store.UpdateAsync(Arg.Any<Role>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("save"); return Task.CompletedTask; });
        var result = await fixture.Change.Handle(request, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Equal(["fresh-authority", "lock", "fresh-authority", "save"], order);
        await fixture.Store.Received(2).FindAsync(fixture.Target.Id, Arg.Any<CancellationToken>());
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan => plan.UserIds.IsEmpty
            && plan.TenantId == Tenant && plan.TenantMode == IdentityTenantLockMode.Shared), Arg.Any<CancellationToken>());
        fixture.Locks.Received(1).RequireCurrent(Arg.Any<IdentityMutationLockPlan>());
        fixture.Reporter.Received(1).ReportRecheck(Arg.Is<ProtectedOperationDecision>(decision => decision.Outcome == ProtectedOperationOutcome.Allowed));
        fixture.Subject.Received(1).Designate(fixture.Target);
        await fixture.Store.Received(1).ValidateGraphAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tenant_admin_edits_take_the_exclusive_plan_before_waiting()
    {
        using var fixture = new Fixture(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var request = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 0, IdentityFoundationManifest.Version1.TenantAdminKernel);
        fixture.Enter(request);
        Assert.True((await fixture.Change.Handle(request, CancellationToken.None)).IsSuccess);
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan => plan.TenantMode == IdentityTenantLockMode.Exclusive),
            Arg.Any<CancellationToken>());
        Assert.True(fixture.Target.IsTenantAdministrator);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("inactive")]
    [InlineData("scope")]
    [InlineData("role_admin")]
    [InlineData("other_key_ceiling")]
    public async Task Unregistered_inactive_incompatible_or_unheld_pairs_cannot_be_authored(string variant)
    {
        using var fixture = new Fixture(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization);
        var definition = variant switch
        {
            "unknown" => new PermissionDefinition(PermissionScope.Organization, "identity.unknown.write"),
            "inactive" => new(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite, isActive: false),
            "scope" => new(PermissionScope.Tenant, IdentityFoundationManifest.MembershipAdmin),
            "role_admin" => new(PermissionScope.Organization, IdentityFoundationManifest.RoleAdmin),
            _ => new(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite),
        };
        if (variant == "other_key_ceiling") fixture.ReturnFacts(fixture.Facts(profile: false));
        var request = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 0, [definition]);
        fixture.Enter(request);
        var result = await fixture.Change.Handle(request, CancellationToken.None);
        Assert.Equal("forbidden", result.Error!.Code);
        Assert.Equal(0, fixture.Target.Version);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        fixture.Reporter.Received(1).ReportResourceRefusal(Arg.Is<ProtectedOperationDecision>(decision =>
            decision.Outcome == ProtectedOperationOutcome.Forbidden && decision.MembershipIds.Contains(Member)), AuthorizationRefusalReason.Delegation);
    }

    [Fact]
    public async Task Current_admin_cannot_restore_its_missing_profile_permission_through_change_or_manifest_adoption()
    {
        using var fixture = new Fixture(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, stripProfile: true);
        fixture.ReturnFacts(fixture.Facts(profile: false));
        var initial = IdentityFoundationManifest.Version1.FindTemplate(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)!.Grants;
        var change = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 1, initial);
        fixture.Enter(change);
        Assert.Equal("forbidden", (await fixture.Change.Handle(change, CancellationToken.None)).Error!.Code);
        var adopt = new AdoptRoleGrantManifestCommand(fixture.Target.Id.Value, 1, 1, IdentityFoundationManifest.Version1.Fingerprint);
        fixture.Enter(adopt);
        Assert.Equal("forbidden", (await fixture.Adopt.Handle(adopt, CancellationToken.None)).Error!.Code);
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        Assert.DoesNotContain(fixture.Target.Grants, grant => grant.PermissionKey == IdentityFoundationManifest.ProfileWrite);
    }

    [Fact]
    public async Task Postwait_revocation_uses_its_new_decision_and_performs_no_mutation()
    {
        using var fixture = new Fixture(BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        var request = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 0, [new(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite)]);
        fixture.Enter(request);
        fixture.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(fixture.Facts(),
            new IdentityAuthorizationResult(IdentityAuthorizationOutcome.NotCovered, Actor, Tenant, null, []));
        var result = await fixture.Change.Handle(request, CancellationToken.None);
        Assert.Equal("not_found", result.Error!.Code);
        await fixture.Locks.Received(1).AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        fixture.Reporter.Received(1).ReportResourceRefusal(Arg.Is<ProtectedOperationDecision>(decision =>
            decision.Outcome == ProtectedOperationOutcome.NotCovered && decision.MembershipIds.IsEmpty && decision.RoleIds.IsEmpty), AuthorizationRefusalReason.Capability);
        fixture.Reporter.DidNotReceiveWithAnyArgs().ReportRecheck(null!);
    }

    [Fact]
    public async Task Version_changes_during_wait_refuse_without_overwriting_or_upgrading_locks()
    {
        using var fixture = new Fixture(BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        var changed = Role.Create(fixture.Target.Id, Tenant, BuiltInRoleKey.Instructor, PermissionScope.Tenant, At, Actor).Value!;
        changed.ReplaceGrants([new(PermissionScope.Tenant, IdentityFoundationManifest.MembershipAdmin)], At.AddSeconds(1), Actor);
        fixture.Store.FindAsync(fixture.Target.Id, Arg.Any<CancellationToken>()).Returns(fixture.Target, changed);
        var request = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 0, []);
        fixture.Enter(request);
        Assert.Equal("concurrency_conflict", (await fixture.Change.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Reader.Received(1).ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>());
        await fixture.Locks.Received(1).AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Theory]
    [InlineData("unadmitted")]
    [InlineData("branch")]
    [InlineData("version")]
    [InlineData("unknown_manifest")]
    public async Task Early_refusals_do_not_reach_locks_or_mutation(string variant)
    {
        using var fixture = new Fixture(BuiltInRoleKey.Instructor, PermissionScope.Tenant, branch: variant == "branch");
        if (variant == "unknown_manifest")
        {
            var adopt = new AdoptRoleGrantManifestCommand(fixture.Target.Id.Value, 0, 2, IdentityFoundationManifest.Version1.Fingerprint);
            fixture.Enter(adopt);
            Assert.Equal("forbidden", (await fixture.Adopt.Handle(adopt, CancellationToken.None)).Error!.Code);
        }
        else
        {
            var change = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, variant == "version" ? 1 : 0, []);
            if (variant != "unadmitted") fixture.Enter(change);
            var result = await fixture.Change.Handle(change, CancellationToken.None);
            Assert.Equal(variant == "version" ? "concurrency_conflict" : "forbidden", result.Error!.Code);
        }
        await fixture.Reader.DidNotReceiveWithAnyArgs().ReadAsync(null!);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Fact]
    public async Task Retained_v1_adoption_derives_the_actual_template_and_obeys_the_same_audited_gate()
    {
        using var fixture = new Fixture(BuiltInRoleKey.Learner, PermissionScope.Organization, stripProfile: true);
        var request = new AdoptRoleGrantManifestCommand(fixture.Target.Id.Value, 1, 1, IdentityFoundationManifest.Version1.Fingerprint);
        fixture.Enter(request);
        var result = await fixture.Adopt.Handle(request, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.True(fixture.Target.MatchesInitialTemplate());
        await fixture.Reader.Received(2).ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>());
        var catalog = new AuditCatalog([new IdentityAuditCatalogSource()]);
        var policies = new OperationPolicyCatalog([new IdentityOperationPolicySource()]);
        foreach (var type in new[] { typeof(ChangeRoleGrantsCommand), typeof(AdoptRoleGrantManifestCommand) })
        {
            Assert.True(catalog.TryGet(type, out var registration));
            var entry = Assert.Single(registration.Entries);
            Assert.Equal(typeof(Role), entry.EntityType);
            Assert.Equal(OperationType.Update, entry.OperationType);
            Assert.Equal(OperationClass.Must, entry.OperationClass);
            Assert.True(policies.TryGet(type, out var policy));
            Assert.Equal(OperationPolicyKind.Protected, policy.Kind);
            Assert.Equal(OperationReadScope.Scoped, policy.ReadScope);
            Assert.True(policy.RequiresOrglessWrite);
            Assert.Equal(IdentityOperationPolicySource.RoleAdministration, policy.RequiredPermission);
            Assert.Equal(entry.Operation, policy.Id);
        }
    }

    [Fact]
    public async Task Unavailable_postwait_authority_remains_dependency_failure_without_a_denial_or_mutation()
    {
        using var fixture = new Fixture(BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        var request = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 0, []);
        fixture.Enter(request);
        fixture.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(fixture.Facts(),
            new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Unavailable, Actor, Tenant, null, []));
        Assert.Equal("dependency_unavailable", (await fixture.Change.Handle(request, CancellationToken.None)).Error!.Code);
        fixture.Reporter.DidNotReceiveWithAnyArgs().ReportResourceRefusal(null!, default);
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CandidateSource _candidates = new();
        private readonly InvocationSource _invocations = new();
        private readonly ITenantContext _context;
        private readonly OperationPolicyCatalog _catalog = new([new IdentityOperationPolicySource()]);
        public Fixture(BuiltInRoleKey key, PermissionScope scope, bool stripProfile = false, bool branch = false)
        {
            _context = TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HasValidatedPrincipal = true,
                HostTenantId = Tenant,
                ClaimTenantId = Tenant,
                UserId = Actor,
                ClaimOrganizationId = branch ? OrganizationId.From(Guid.CreateVersion7()) : null,
                MembershipCovers = true,
                ClaimedOrganizationBelongsToTenant = true,
            }).Value!;
            Target = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, key, scope, At, Actor).Value!;
            if (stripProfile) Target.ReplaceGrants(Target.Grants.Where(grant => grant.PermissionKey != IdentityFoundationManifest.ProfileWrite)
                .Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)).ToArray(), At.AddSeconds(1), Actor);
            Store.FindAsync(Target.Id, Arg.Any<CancellationToken>()).Returns(Target);
            ReturnFacts(Facts());
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(At.AddSeconds(2));
            var authorization = new RoleGrantAuthorization(Reader, _catalog, Current, _context, Reporter);
            Change = new(Store, _context, Locks, Current, () => authorization, Subject, clock);
            Adopt = new(Store, _context, Locks, Current, () => authorization, Subject, clock);
        }
        public Role Target { get; }
        public IRoleWriteStore Store { get; } = Substitute.For<IRoleWriteStore>();
        public IIdentityAuthorizationReader Reader { get; } = Substitute.For<IIdentityAuthorizationReader>();
        public IIdentityMutationLocks Locks { get; } = Substitute.For<IIdentityMutationLocks>();
        public IOperationInvocationContext Current { get; } = Substitute.For<IOperationInvocationContext>();
        public IAuthorizationRefusalReporter Reporter { get; } = Substitute.For<IAuthorizationRefusalReporter>();
        public IAuditSubject Subject { get; } = Substitute.For<IAuditSubject>();
        public ChangeRoleGrantsCommandHandler Change { get; }
        public AdoptRoleGrantManifestCommandHandler Adopt { get; }
        public void Enter(object request)
        {
            Assert.True(_catalog.TryGet(request.GetType(), out var policy));
            var candidate = _candidates.Mint(_context);
            var invocation = _invocations.Mint(request, policy, _context, candidate);
            Current.CurrentInvocation.Returns(invocation);
            Current.CurrentDecision.Returns(DecisionSource.Allowed(invocation, candidate));
        }
        public IdentityAuthorizationResult Facts(bool profile = true)
        {
            var grants = profile ? IdentityFoundationManifest.Version1.FindTemplate(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)!.Grants
                : IdentityFoundationManifest.Version1.TenantAdminKernel;
            return new(IdentityAuthorizationOutcome.Covered, Actor, Target.TenantId, null,
                [new(Member, null, [new(AdminRole, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, grants)])]);
        }
        public void ReturnFacts(IdentityAuthorizationResult result) => Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(result);
        public void Dispose() => _candidates.Dispose();
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate Mint(ITenantContext context) => ForCandidate(AuthorizationSourceKind.Http, Actor, Tenant, context.OrganizationId);
    }
    private sealed class InvocationSource : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        public OperationInvocation Mint(object request, OperationPolicy policy, ITenantContext context, AuthorizationCandidate candidate)
        {
            _current = ForInvocation(request, policy, context, candidate, null, CancellationToken.None);
            return _current;
        }
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }
    private sealed class DecisionSource : ProtectedOperationDecision.MintingSource
    {
        public static ProtectedOperationDecision Allowed(OperationInvocation invocation, AuthorizationCandidate candidate) =>
            ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, [Member], [AdminRole]);
    }
}

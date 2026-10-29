using System.Collections.Immutable;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class RoleReleaseCommandTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly DateTimeOffset At = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_v2_adoption_uses_exact_new_key_exception_and_fresh_authority_after_Tenant_and_Role_waits()
    {
        using var fixture = new Fixture(stripProfile: true);
        var request = fixture.Adoption();
        fixture.Enter(request);
        var order = new List<string>();
        fixture.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(_ =>
        { order.Add("authority"); return fixture.Facts(); });
        fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(_ =>
        { order.Add("tenant"); return Task.CompletedTask; });
        fixture.Store.LockAsync(fixture.Target.Id, Arg.Any<CancellationToken>()).Returns(_ =>
        { order.Add("role"); return Task.CompletedTask; });
        fixture.Store.UpdateAsync(fixture.Target, Arg.Any<CancellationToken>()).Returns(_ =>
        { order.Add("save"); return Task.CompletedTask; });
        var result = await fixture.Adopt.Handle(request, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.Equal(["authority", "tenant", "role", "authority", "save"], order);
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan =>
            plan.UserIds.IsEmpty && plan.TenantId == Tenant && plan.TenantMode == IdentityTenantLockMode.Exclusive), Arg.Any<CancellationToken>());
        Assert.Equal(2, fixture.Target.AdoptedReleaseVersion);
        Assert.DoesNotContain(fixture.Target.Grants, grant => grant.PermissionKey == IdentityFoundationManifest.ProfileWrite);
        Assert.All(IdentityRoleRelease.Version2.TenantAdminDelta, pair => Assert.Contains(fixture.Target.Grants,
            grant => grant.PermissionScope == pair.Scope && grant.PermissionKey == pair.Key));
    }

    [Fact]
    public async Task Ordinary_edits_have_no_initial_key_exception_and_no_profile_self_escalation()
    {
        using var fixture = new Fixture(stripProfile: true);
        var newKeys = fixture.Target.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey))
            .Concat(IdentityRoleRelease.Version2.TenantAdminDelta).ToImmutableArray();
        var change = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, fixture.Target.Version, newKeys);
        fixture.Enter(change);
        Assert.Equal("forbidden", (await fixture.Change.Handle(change, CancellationToken.None)).Error!.Code);
        var profile = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, fixture.Target.Version,
            IdentityFoundationManifest.Version1.FindTemplate(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)!.Grants);
        fixture.Enter(profile);
        Assert.Equal("forbidden", (await fixture.Change.Handle(profile, CancellationToken.None)).Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Fact]
    public async Task Adopt_remove_and_readopt_refuses_both_the_repeat_exception_and_unheld_ordinary_restore()
    {
        using var fixture = new Fixture();
        var adopt = fixture.Adoption();
        fixture.Enter(adopt);
        Assert.True((await fixture.Adopt.Handle(adopt, CancellationToken.None)).IsSuccess);
        var removed = IdentityRoleRelease.Version2.TenantAdminDelta[0];
        var remaining = fixture.Target.Grants.Where(grant => grant.PermissionKey != removed.Key)
            .Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)).ToImmutableArray();
        var remove = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 1, remaining);
        fixture.Enter(remove);
        Assert.True((await fixture.Change.Handle(remove, CancellationToken.None)).IsSuccess);
        var repeat = fixture.Adoption();
        fixture.Enter(repeat);
        Assert.Equal("forbidden", (await fixture.Adopt.Handle(repeat, CancellationToken.None)).Error!.Code);
        var restore = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, 2, remaining.Add(removed));
        fixture.Enter(restore);
        Assert.Equal("forbidden", (await fixture.Change.Handle(restore, CancellationToken.None)).Error!.Code);
        Assert.Equal(2, fixture.Target.Version);
        Assert.Equal(2, fixture.Target.AdoptedReleaseVersion);
        Assert.DoesNotContain(fixture.Target.Grants, grant => grant.PermissionKey == removed.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Postwait_revocation_or_changed_Role_version_refuses_before_adoption(bool changedVersion)
    {
        using var fixture = new Fixture();
        var request = fixture.Adoption();
        fixture.Enter(request);
        if (changedVersion)
        {
            var changed = Role.Create(fixture.Target.Id, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, At, Actor).Value!;
            changed.ReplaceGrants(IdentityFoundationManifest.Version1.TenantAdminKernel, At.AddSeconds(1), Actor);
            fixture.Store.FindAsync(fixture.Target.Id, Arg.Any<CancellationToken>()).Returns(fixture.Target, changed);
        }
        else fixture.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(fixture.Facts(),
            new IdentityAuthorizationResult(IdentityAuthorizationOutcome.NotCovered, Actor, Tenant, null, []));
        Assert.Equal(changedVersion ? "concurrency_conflict" : "not_found", (await fixture.Adopt.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Store.Received(1).LockAsync(fixture.Target.Id, Arg.Any<CancellationToken>());
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        Assert.Equal(1, fixture.Target.AdoptedReleaseVersion);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("fingerprint")]
    [InlineData("release")]
    [InlineData("branch")]
    public async Task Stale_or_unknown_release_and_branch_context_never_enter_the_lock_plan(string variant)
    {
        using var fixture = new Fixture(branch: variant == "branch");
        var release = IdentityRoleRelease.Version2;
        var request = new AdoptRoleGrantManifestCommand(fixture.Target.Id.Value, variant == "version" ? 99 : 0,
            variant == "release" ? 3 : release.Version, variant == "fingerprint" ? IdentityFoundationManifest.Version1.Fingerprint : release.Fingerprint);
        fixture.Enter(request);
        Assert.Equal(variant == "version" ? "concurrency_conflict" : "forbidden", (await fixture.Adopt.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Store.DidNotReceiveWithAnyArgs().LockAsync(fixture.Target.Id);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CandidateSource _candidates = new();
        private readonly InvocationSource _invocations = new();
        private readonly ITenantContext _context;
        private readonly OperationPolicyCatalog _catalog = new([new IdentityOperationPolicySource(), new TenancyOperationPolicySource()]);
        public Fixture(bool stripProfile = false, bool branch = false)
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
            Target = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, At, Actor).Value!;
            if (stripProfile) Target.ReplaceGrants(IdentityFoundationManifest.Version1.TenantAdminKernel, At.AddSeconds(1), Actor);
            Store.FindAsync(Target.Id, Arg.Any<CancellationToken>()).Returns(Target);
            Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(_ => Facts());
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
        public AdoptRoleGrantManifestCommand Adoption() => new(Target.Id.Value, Target.Version,
            IdentityRoleRelease.Version2.Version, IdentityRoleRelease.Version2.Fingerprint);
        public void Enter(object request)
        {
            Assert.True(_catalog.TryGet(request.GetType(), out var policy));
            var candidate = _candidates.Mint(_context);
            var invocation = _invocations.Mint(request, policy, _context, candidate);
            Current.CurrentInvocation.Returns(invocation);
            Current.CurrentDecision.Returns(DecisionSource.Allowed(invocation, candidate, Target.Id.Value));
        }
        public IdentityAuthorizationResult Facts() => new(IdentityAuthorizationOutcome.Covered, Actor, Tenant, null,
            [new(Member, null, [new(Target.Id.Value, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant,
                [.. Target.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey))])])]);
        public void Dispose() => _candidates.Dispose();
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate Mint(ITenantContext context) => ForCandidate(AuthorizationSourceKind.Http, Actor, Tenant, context.OrganizationId);
    }
    private sealed class InvocationSource : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        public OperationInvocation Mint(object request, OperationPolicy policy, ITenantContext context, AuthorizationCandidate candidate) =>
            _current = ForInvocation(request, policy, context, candidate, null, CancellationToken.None);
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }
    private sealed class DecisionSource : ProtectedOperationDecision.MintingSource
    {
        public static ProtectedOperationDecision Allowed(OperationInvocation invocation, AuthorizationCandidate candidate, Guid role) =>
            ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, [Member], [role]);
    }
}

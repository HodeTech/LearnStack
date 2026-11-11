using System.Collections.Immutable;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Roles;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Application;
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

public sealed class RoleReleaseV3Tests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly DateTimeOffset At = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void V3_adds_one_Tenant_pair_retains_historical_fingerprints_and_does_not_expand_new_seed_Roles()
    {
        var initial = IdentityFoundationManifest.Version1;
        var previous = IdentityRoleRelease.Version2;
        var current = IdentityRoleRelease.Version3;
        Assert.Equal("ba16d2c21f2547f3dc7f9dce24e25a18f30103186739ea00a33e9edf43834794", initial.Fingerprint);
        Assert.Equal("abc5b3e06a6cbdf0f6bb3abadc172c7c9ecfe0fcbd28ba4529d1e60100c6a0ff", previous.Fingerprint);
        Assert.Equal("1645d43001d88592535ee022e2044702cc2929c98d6d5959083016b0a5067cd9", current.Fingerprint);
        Assert.Same(current, IdentityRoleRelease.Current);
        Assert.Equal(9, current.Definitions.Length);
        Assert.Equal(BoundedCustomFieldPermissions.Definition, Assert.Single(current.TenantAdminDelta));
        Assert.Equal(previous.Definitions.Concat(current.TenantAdminDelta), current.Definitions);
        Assert.False(current.IsActive(PermissionScope.Organization, BoundedCustomFieldPermissions.Definition.Key));
        Assert.False(current.IsActive(PermissionScope.Platform, BoundedCustomFieldPermissions.Definition.Key));
        foreach (var template in initial.Templates)
        {
            var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, template.BuiltInKey, template.Scope, At, Actor).Value!;
            Assert.True(role.MatchesInitialTemplate());
            Assert.Equal(1, role.AdoptedReleaseVersion);
            Assert.DoesNotContain(role.Grants, grant => grant.PermissionKey == BoundedCustomFieldPermissions.Definition.Key);
        }
    }

    [Fact]
    public void Consecutive_adoption_preserves_removed_v1_and_v2_grants_and_retained_kernel_instances()
    {
        using var fixture = new Fixture(removeOlderGrants: true);
        var role = fixture.Target;
        var retained = role.Grants.ToArray();
        var release = IdentityRoleRelease.Version3;
        Assert.Same(release, IdentityRoleRelease.NextFor(role));
        Assert.True(role.AdoptRelease(release.Version, release.Fingerprint, At.AddMinutes(1), Actor).IsSuccess);
        Assert.Equal(3, role.AdoptedReleaseVersion);
        Assert.Equal(release.Fingerprint, role.AdoptedReleaseFingerprint);
        Assert.Equal(IdentityFoundationManifest.Version1.TenantAdminKernel.Concat(release.TenantAdminDelta).OrderBy(pair => pair.Key),
            role.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)).OrderBy(pair => pair.Key));
        Assert.All(retained, grant => Assert.Contains(role.Grants, current => ReferenceEquals(grant, current)));
        Assert.Null(IdentityRoleRelease.NextFor(role));
        Assert.True(role.ReplaceGrants(IdentityFoundationManifest.Version1.TenantAdminKernel, At.AddMinutes(2), Actor).IsSuccess);
        Assert.True(role.AdoptRelease(release.Version, release.Fingerprint, At.AddMinutes(3), Actor).IsFailure);
        Assert.DoesNotContain(role.Grants, grant => grant.PermissionKey == BoundedCustomFieldPermissions.Definition.Key);
    }

    [Fact]
    public void Direct_v1_to_v3_and_non_TenantAdmin_adoption_refuse_without_mutation()
    {
        var release = IdentityRoleRelease.Version3;
        foreach (var template in IdentityFoundationManifest.Version1.Templates)
        {
            var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, template.BuiltInKey, template.Scope, At, Actor).Value!;
            var before = role.Grants.ToArray();
            Assert.True(role.AdoptRelease(release.Version, release.Fingerprint, At.AddMinutes(1), Actor).IsFailure);
            Assert.Equal(0, role.Version);
            Assert.Equal(1, role.AdoptedReleaseVersion);
            Assert.Equal(before, role.Grants);
        }
    }

    [Fact]
    public void Invalid_or_unknown_markers_are_not_valid_installed_state()
    {
        var release = IdentityRoleRelease.Current;
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 2, release.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 3, IdentityRoleRelease.Version2.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.Editor, PermissionScope.Tenant, 3, release.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.OrgAdmin, PermissionScope.Organization, 3, release.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 4, release.Fingerprint));
    }

    [Fact]
    public void Valid_v3_warm_history_with_removed_delta_is_installed_without_becoming_pristine()
    {
        var release = IdentityRoleRelease.Version3;
        var id = Guid.CreateVersion7();
        var grants = IdentityFoundationManifest.Version1.TenantAdminKernel;
        var row = new RoleSeedDto(id, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 4, null, null, grants,
            new(At, UserId.SystemActor, At.AddMinutes(1), Actor), release.Version, release.Fingerprint, GraphIsValid: true);
        Assert.True(SeedIdentityInstallationEvidence.RoleIsInstalled(row, id, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant));
        Assert.False(SeedIdentityInstallationEvidence.RoleIsPristine(row));
        Assert.Equal(grants, row.Grants);
        Assert.False(SeedIdentityInstallationEvidence.RoleIsInstalled(row with { AdoptedReleaseFingerprint = IdentityRoleRelease.Version2.Fingerprint },
            id, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant));
    }

    [Fact]
    public async Task V3_adoption_uses_only_the_one_pair_exception_and_refreshes_authority_after_exclusive_waits()
    {
        using var fixture = new Fixture(removeOlderGrants: true);
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
        Assert.Equal(3, result.Value);
        Assert.Equal(["authority", "tenant", "role", "authority", "save"], order);
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan =>
            plan.UserIds.IsEmpty && plan.TenantId == Tenant && plan.TenantMode == IdentityTenantLockMode.Exclusive), Arg.Any<CancellationToken>());
        Assert.Equal(3, fixture.Target.AdoptedReleaseVersion);
        Assert.Equal(3, fixture.Target.Grants.Count);
        Assert.Contains(fixture.Target.Grants, grant => grant.PermissionKey == BoundedCustomFieldPermissions.Definition.Key);
    }

    [Fact]
    public async Task Ordinary_edit_cannot_borrow_the_v3_adoption_exception()
    {
        using var fixture = new Fixture();
        var request = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, fixture.Target.Version,
            [.. fixture.Target.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)),
                BoundedCustomFieldPermissions.Definition]);
        fixture.Enter(request);
        Assert.Equal("forbidden", (await fixture.Change.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        Assert.Equal(2, fixture.Target.AdoptedReleaseVersion);
    }

    [Fact]
    public async Task Adoption_requires_the_owning_module_registration_and_never_infers_it_from_release_definitions()
    {
        using var fixture = new Fixture(registerCustomFields: false);
        var request = fixture.Adoption();
        fixture.Enter(request);
        Assert.Equal("forbidden", (await fixture.Adopt.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        Assert.Equal(2, fixture.Target.AdoptedReleaseVersion);
    }

    [Fact]
    public async Task V3_replay_and_unheld_ordinary_restore_refuse_after_the_delta_is_removed()
    {
        using var fixture = new Fixture();
        var adoption = fixture.Adoption();
        fixture.Enter(adoption);
        Assert.True((await fixture.Adopt.Handle(adoption, CancellationToken.None)).IsSuccess);
        var retained = fixture.Target.Grants.Where(grant => grant.PermissionKey != BoundedCustomFieldPermissions.Definition.Key)
            .Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)).ToImmutableArray();
        var removal = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, fixture.Target.Version, retained);
        fixture.Enter(removal);
        Assert.True((await fixture.Change.Handle(removal, CancellationToken.None)).IsSuccess);
        var replay = fixture.Adoption();
        fixture.Enter(replay);
        Assert.Equal("forbidden", (await fixture.Adopt.Handle(replay, CancellationToken.None)).Error!.Code);
        var restore = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, fixture.Target.Version, retained.Add(BoundedCustomFieldPermissions.Definition));
        fixture.Enter(restore);
        Assert.Equal("forbidden", (await fixture.Change.Handle(restore, CancellationToken.None)).Error!.Code);
        Assert.Equal(3, fixture.Target.AdoptedReleaseVersion);
        Assert.DoesNotContain(fixture.Target.Grants, grant => grant.PermissionKey == BoundedCustomFieldPermissions.Definition.Key);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("fingerprint")]
    [InlineData("stale")]
    [InlineData("branch")]
    public async Task Invalid_source_request_or_scope_refuses_v3_before_locking(string variant)
    {
        using var fixture = new Fixture(atVersion2: variant != "skip", branch: variant == "branch");
        var request = fixture.Adoption() with
        {
            ExpectedRoleVersion = variant == "stale" ? 99 : fixture.Target.Version,
            ManifestFingerprint = variant == "fingerprint" ? IdentityRoleRelease.Version2.Fingerprint : IdentityRoleRelease.Version3.Fingerprint,
        };
        fixture.Enter(request);
        Assert.Equal(variant == "stale" ? "concurrency_conflict" : "forbidden",
            (await fixture.Adopt.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Fact]
    public async Task Postwait_revocation_refuses_v3_before_marker_or_grant_changes()
    {
        using var fixture = new Fixture();
        var request = fixture.Adoption();
        fixture.Enter(request);
        fixture.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(fixture.Facts(),
            new IdentityAuthorizationResult(IdentityAuthorizationOutcome.NotCovered, Actor, Tenant, null, []));
        Assert.Equal("not_found", (await fixture.Adopt.Handle(request, CancellationToken.None)).Error!.Code);
        Assert.Equal(2, fixture.Target.AdoptedReleaseVersion);
        Assert.DoesNotContain(fixture.Target.Grants, grant => grant.PermissionKey == BoundedCustomFieldPermissions.Definition.Key);
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Theory]
    [InlineData(1, 2, 8)]
    [InlineData(2, 3, 9)]
    public async Task Registry_discovers_both_exact_transitions_while_preserving_legacy_default_shape(int source, int target, int count)
    {
        using var fixture = new Fixture();
        var request = source == 1 ? new GetPermissionRegistryQuery() : new GetPermissionRegistryQuery(source);
        fixture.Enter(request);
        var result = await fixture.Registry.Handle(request, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(target, result.Value!.Release.Version);
        Assert.Equal(IdentityRoleRelease.Find(target)!.Fingerprint, result.Value.Release.Fingerprint);
        Assert.Equal(count, result.Value.Items.Length);
        if (source == 2)
        {
            var item = Assert.Single(result.Value.Items, item => item.Key == BoundedCustomFieldPermissions.Definition.Key);
            Assert.Equal(PermissionScope.Tenant, item.Scope);
            Assert.False(item.CanGrant);
            Assert.Equal(BoundedCustomFieldPermissions.Definition, Assert.Single(result.Value.Release.TenantAdminDelta));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public async Task Registry_unknown_source_refuses_without_exposing_a_release(int source)
    {
        using var fixture = new Fixture();
        var request = new GetPermissionRegistryQuery(source);
        fixture.Enter(request);
        Assert.Equal("validation_failed", (await fixture.Registry.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Reader.DidNotReceiveWithAnyArgs().ReadAsync(null!);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CandidateSource _candidates = new();
        private readonly InvocationSource _invocations = new();
        private readonly ITenantContext _context;
        private readonly OperationPolicyCatalog _catalog;
        public Fixture(bool atVersion2 = true, bool removeOlderGrants = false, bool branch = false, bool registerCustomFields = true)
        {
            _catalog = registerCustomFields
                ? new([new IdentityOperationPolicySource(), new TenancyOperationPolicySource(), new CustomizationOperationPolicySource()])
                : new([new IdentityOperationPolicySource(), new TenancyOperationPolicySource()]);
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
            if (atVersion2) Assert.True(Target.AdoptRelease(2, IdentityRoleRelease.Version2.Fingerprint, At.AddSeconds(1), Actor).IsSuccess);
            if (removeOlderGrants) Assert.True(Target.ReplaceGrants(IdentityFoundationManifest.Version1.TenantAdminKernel, At.AddSeconds(2), Actor).IsSuccess);
            Store.FindAsync(Target.Id, Arg.Any<CancellationToken>()).Returns(Target);
            Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(_ => Facts());
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(At.AddSeconds(3));
            var authorization = new RoleGrantAuthorization(Reader, _catalog, Current, _context, Reporter);
            Change = new(Store, _context, Locks, Current, () => authorization, Subject, clock);
            Adopt = new(Store, _context, Locks, Current, () => authorization, Subject, clock);
            var authorizer = Substitute.For<IProtectedOperationAuthorizer>();
            authorizer.AuthorizeAsync(Arg.Any<OperationInvocation>(), Arg.Any<OperationPolicy>(), Arg.Any<AuthorizationCandidate>(),
                Arg.Any<CancellationToken>()).Returns(call => DecisionSource.Allowed(call.ArgAt<OperationInvocation>(0),
                    call.ArgAt<AuthorizationCandidate>(2), Target.Id.Value));
            Registry = new(new IdentityAdministrationReadAuthorization(_catalog, Current, _context, authorizer, Reader, Reporter), _catalog);
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
        public GetPermissionRegistryQueryHandler Registry { get; }
        public AdoptRoleGrantManifestCommand Adoption() => new(Target.Id.Value, Target.Version,
            IdentityRoleRelease.Version3.Version, IdentityRoleRelease.Version3.Fingerprint);
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

using System.Collections.Immutable;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Memberships;
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

/// <summary>Registry release v4 (ADR-0099): one Identity-owned federation pair, adopted only after exact v3.</summary>
public sealed class RoleReleaseV4Tests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly DateTimeOffset At = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void V4_adds_only_the_federation_pair_with_a_pinned_fingerprint_and_chains_from_exact_v3()
    {
        var previous = IdentityRoleRelease.Version3;
        var current = IdentityRoleRelease.Version4;
        Assert.Equal("1645d43001d88592535ee022e2044702cc2929c98d6d5959083016b0a5067cd9", previous.Fingerprint);
        Assert.Equal("b66e96e3f4692c530d99c1ee3e083a58f9872696f2aeb3082195ac8fd2360909", current.Fingerprint);
        Assert.Same(current, IdentityRoleRelease.Current);
        Assert.Same(current, IdentityRoleRelease.Find(4));
        Assert.Null(IdentityRoleRelease.Find(5));
        Assert.Equal(4, current.Version);
        Assert.Equal(10, current.Definitions.Length);
        Assert.Equal(FederationPermissions.Definition, Assert.Single(current.TenantAdminDelta));
        Assert.Equal(new PermissionDefinition(PermissionScope.Tenant, "identity.federation.admin"), FederationPermissions.Definition);
        Assert.Equal(previous.Definitions.Concat(current.TenantAdminDelta), current.Definitions);
        Assert.True(current.IsActive(PermissionScope.Tenant, FederationPermissions.Definition.Key));
        Assert.False(current.IsActive(PermissionScope.Organization, FederationPermissions.Definition.Key));
        Assert.False(previous.IsActive(PermissionScope.Tenant, FederationPermissions.Definition.Key));
        foreach (var template in IdentityFoundationManifest.Version1.Templates)
        {
            var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, template.BuiltInKey, template.Scope, At, Actor).Value!;
            Assert.Equal(1, role.AdoptedReleaseVersion);
            Assert.DoesNotContain(role.Grants, grant => grant.PermissionKey == FederationPermissions.Definition.Key);
        }
    }

    [Fact]
    public void NextFor_walks_v1_v2_v3_v4_in_order_and_preserves_every_earlier_removal()
    {
        var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, At, Actor).Value!;
        var expected = new[] { IdentityRoleRelease.Version2, IdentityRoleRelease.Version3, IdentityRoleRelease.Version4 };
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Same(expected[index], IdentityRoleRelease.NextFor(role));
            Assert.True(role.AdoptRelease(expected[index].Version, expected[index].Fingerprint, At.AddMinutes(index * 2 + 1), Actor).IsSuccess);
            // Removing the optional grants after each step must survive later adoptions.
            Assert.True(role.ReplaceGrants(IdentityFoundationManifest.Version1.TenantAdminKernel, At.AddMinutes(index * 2 + 2), Actor).IsSuccess);
        }
        Assert.Null(IdentityRoleRelease.NextFor(role));
        Assert.Equal(4, role.AdoptedReleaseVersion);
        Assert.Equal(IdentityRoleRelease.Version4.Fingerprint, role.AdoptedReleaseFingerprint);
        Assert.Equal(IdentityFoundationManifest.Version1.TenantAdminKernel.OrderBy(pair => pair.Key),
            role.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)).OrderBy(pair => pair.Key));
        Assert.True(role.AdoptRelease(4, IdentityRoleRelease.Version4.Fingerprint, At.AddHours(1), Actor).IsFailure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Skipping_to_v4_from_v1_or_v2_refuses_without_mutation(int source)
    {
        var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, At, Actor).Value!;
        if (source == 2) Assert.True(role.AdoptRelease(2, IdentityRoleRelease.Version2.Fingerprint, At.AddMinutes(1), Actor).IsSuccess);
        var version = role.Version;
        var grants = role.Grants.ToArray();
        Assert.True(role.AdoptRelease(4, IdentityRoleRelease.Version4.Fingerprint, At.AddMinutes(2), Actor).IsFailure);
        Assert.Equal(version, role.Version);
        Assert.Equal(source, role.AdoptedReleaseVersion);
        Assert.Equal(grants, role.Grants);
    }

    [Fact]
    public void Non_TenantAdmin_templates_never_adopt_v4()
    {
        foreach (var template in IdentityFoundationManifest.Version1.Templates.Where(item => item.BuiltInKey != BuiltInRoleKey.TenantAdmin))
        {
            var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, template.BuiltInKey, template.Scope, At, Actor).Value!;
            // NextFor names only the marker successor; the template check is CanFirstAdopt's.
            Assert.True(role.AdoptRelease(2, IdentityRoleRelease.Version2.Fingerprint, At.AddMinutes(1), Actor).IsFailure);
            Assert.True(role.AdoptRelease(4, IdentityRoleRelease.Version4.Fingerprint, At.AddMinutes(1), Actor).IsFailure);
            Assert.Equal(1, role.AdoptedReleaseVersion);
        }
    }

    [Fact]
    public void Only_exact_v4_markers_on_the_Tenant_Admin_template_are_valid_installed_state()
    {
        var release = IdentityRoleRelease.Current;
        Assert.True(release.IsValidAdoptionState(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 4, IdentityRoleRelease.Version4.Fingerprint));
        Assert.True(release.IsValidAdoptionState(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 3, IdentityRoleRelease.Version3.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 4, IdentityRoleRelease.Version3.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 3, IdentityRoleRelease.Version4.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.OrgAdmin, PermissionScope.Organization, 4, IdentityRoleRelease.Version4.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.Editor, PermissionScope.Tenant, 4, IdentityRoleRelease.Version4.Fingerprint));
        Assert.False(release.IsValidAdoptionState(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 5, IdentityRoleRelease.Version4.Fingerprint));
    }

    [Fact]
    public void Valid_v4_warm_history_with_removed_delta_is_installed_without_becoming_pristine()
    {
        var release = IdentityRoleRelease.Version4;
        var id = Guid.CreateVersion7();
        var grants = IdentityFoundationManifest.Version1.TenantAdminKernel;
        var row = new RoleSeedDto(id, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 6, null, null, grants,
            new(At, UserId.SystemActor, At.AddMinutes(1), Actor), release.Version, release.Fingerprint, GraphIsValid: true);
        Assert.True(SeedIdentityInstallationEvidence.RoleIsInstalled(row, id, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant));
        Assert.False(SeedIdentityInstallationEvidence.RoleIsPristine(row));
        Assert.False(SeedIdentityInstallationEvidence.RoleIsInstalled(row with { AdoptedReleaseFingerprint = IdentityRoleRelease.Version3.Fingerprint },
            id, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant));
    }

    [Fact]
    public async Task V4_adoption_adds_only_the_federation_pair_after_exact_v3()
    {
        using var fixture = new Fixture();
        var request = fixture.Adoption();
        fixture.Enter(request);
        var before = fixture.Target.Grants.Select(grant => grant.PermissionKey).ToArray();
        var result = await fixture.Adopt.Handle(request, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(4, fixture.Target.AdoptedReleaseVersion);
        Assert.Equal(IdentityRoleRelease.Version4.Fingerprint, fixture.Target.AdoptedReleaseFingerprint);
        Assert.Equal(before.Append(FederationPermissions.Definition.Key).Order(StringComparer.Ordinal),
            fixture.Target.Grants.Select(grant => grant.PermissionKey).Order(StringComparer.Ordinal));
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan =>
            plan.TenantId == Tenant && plan.TenantMode == IdentityTenantLockMode.Exclusive), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ordinary_edit_cannot_borrow_the_v4_adoption_exception()
    {
        using var fixture = new Fixture();
        var request = new ChangeRoleGrantsCommand(fixture.Target.Id.Value, fixture.Target.Version,
            [.. fixture.Target.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)),
                FederationPermissions.Definition]);
        fixture.Enter(request);
        Assert.Equal("forbidden", (await fixture.Change.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        Assert.Equal(3, fixture.Target.AdoptedReleaseVersion);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("fingerprint")]
    [InlineData("stale")]
    [InlineData("branch")]
    public async Task Invalid_source_request_or_scope_refuses_v4_before_locking(string variant)
    {
        using var fixture = new Fixture(atVersion3: variant != "skip", branch: variant == "branch");
        var request = fixture.Adoption() with
        {
            ExpectedRoleVersion = variant == "stale" ? 99 : fixture.Target.Version,
            ManifestFingerprint = variant == "fingerprint" ? IdentityRoleRelease.Version3.Fingerprint : IdentityRoleRelease.Version4.Fingerprint,
        };
        fixture.Enter(request);
        Assert.Equal(variant == "stale" ? "concurrency_conflict" : "forbidden",
            (await fixture.Adopt.Handle(request, CancellationToken.None)).Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Store.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        Assert.DoesNotContain(fixture.Target.Grants, grant => grant.PermissionKey == FederationPermissions.Definition.Key);
    }

    [Fact]
    public async Task Registry_source_three_discovers_exact_v4_with_ten_pairs()
    {
        using var fixture = new Fixture();
        var request = new GetPermissionRegistryQuery(3);
        fixture.Enter(request);
        var result = await fixture.Registry.Handle(request, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value!.Release.Version);
        Assert.Equal(IdentityRoleRelease.Version4.Fingerprint, result.Value.Release.Fingerprint);
        Assert.Equal(10, result.Value.Items.Length);
        var item = Assert.Single(result.Value.Items, item => item.Key == FederationPermissions.Definition.Key);
        Assert.Equal(PermissionScope.Tenant, item.Scope);
        Assert.Equal("lockey_permission_tenant_identity_federation_admin", item.LabelKey);
        Assert.False(item.CanGrant);
        Assert.Equal(FederationPermissions.Definition, Assert.Single(result.Value.Release.TenantAdminDelta));
    }

    [Fact]
    public async Task Eleventh_advisory_capability_follows_the_held_Tenant_pair_and_is_false_in_a_branch()
    {
        using (var fixture = new Fixture())
        {
            var request = new GetOwnCapabilitiesQuery();
            fixture.Enter(request);
            var before = await fixture.Capabilities.Handle(request, CancellationToken.None);
            Assert.True(before.IsSuccess);
            Assert.False(before.Value!.CanManageFederation);
            var adoption = fixture.Adoption();
            fixture.Enter(adoption);
            Assert.True((await fixture.Adopt.Handle(adoption, CancellationToken.None)).IsSuccess);
            fixture.Enter(request);
            var after = await fixture.Capabilities.Handle(request, CancellationToken.None);
            Assert.True(after.IsSuccess);
            Assert.True(after.Value!.CanManageFederation);
            Assert.True(after.Value.CanManageCustomFields);
        }
        using (var branch = new Fixture(branch: true, adoptVersion4: true))
        {
            var request = new GetOwnCapabilitiesQuery();
            branch.Enter(request);
            var result = await branch.Capabilities.Handle(request, CancellationToken.None);
            Assert.True(result.IsSuccess);
            Assert.False(result.Value!.CanManageFederation);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CandidateSource _candidates = new();
        private readonly InvocationSource _invocations = new();
        private readonly ITenantContext _context;
        private readonly OperationPolicyCatalog _catalog;
        private readonly OrganizationId? _organization;
        public Fixture(bool atVersion3 = true, bool branch = false, bool adoptVersion4 = false)
        {
            _organization = branch ? OrganizationId.From(Guid.CreateVersion7()) : null;
            _catalog = new([new IdentityOperationPolicySource(), new TenancyOperationPolicySource(), new CustomizationOperationPolicySource()]);
            _context = TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HasValidatedPrincipal = true,
                HostTenantId = Tenant,
                ClaimTenantId = Tenant,
                UserId = Actor,
                ClaimOrganizationId = _organization,
                MembershipCovers = true,
                ClaimedOrganizationBelongsToTenant = true,
            }).Value!;
            Target = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, At, Actor).Value!;
            Assert.True(Target.AdoptRelease(2, IdentityRoleRelease.Version2.Fingerprint, At.AddSeconds(1), Actor).IsSuccess);
            if (atVersion3) Assert.True(Target.AdoptRelease(3, IdentityRoleRelease.Version3.Fingerprint, At.AddSeconds(2), Actor).IsSuccess);
            if (adoptVersion4) Assert.True(Target.AdoptRelease(4, IdentityRoleRelease.Version4.Fingerprint, At.AddSeconds(3), Actor).IsSuccess);
            Store.FindAsync(Target.Id, Arg.Any<CancellationToken>()).Returns(Target);
            Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(_ => Facts());
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(At.AddSeconds(4));
            var authorization = new RoleGrantAuthorization(Reader, _catalog, Current, _context, Reporter);
            Change = new(Store, _context, Locks, Current, () => authorization, Subject, clock);
            Adopt = new(Store, _context, Locks, Current, () => authorization, Subject, clock);
            var authorizer = Substitute.For<IProtectedOperationAuthorizer>();
            authorizer.AuthorizeAsync(Arg.Any<OperationInvocation>(), Arg.Any<OperationPolicy>(), Arg.Any<AuthorizationCandidate>(),
                Arg.Any<CancellationToken>()).Returns(call => DecisionSource.Allowed(call.ArgAt<OperationInvocation>(0),
                    call.ArgAt<AuthorizationCandidate>(2), Target.Id.Value));
            var reads = new IdentityAdministrationReadAuthorization(_catalog, Current, _context, authorizer, Reader, Reporter);
            Registry = new(reads, _catalog);
            Capabilities = new(reads, _catalog);
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
        public GetOwnCapabilitiesQueryHandler Capabilities { get; }
        public AdoptRoleGrantManifestCommand Adoption() => new(Target.Id.Value, Target.Version,
            IdentityRoleRelease.Version4.Version, IdentityRoleRelease.Version4.Fingerprint);
        public void Enter(object request)
        {
            Assert.True(_catalog.TryGet(request.GetType(), out var policy));
            var candidate = _candidates.Mint(_context);
            var invocation = _invocations.Mint(request, policy, _context, candidate);
            Current.CurrentInvocation.Returns(invocation);
            Current.CurrentDecision.Returns(DecisionSource.Allowed(invocation, candidate, Target.Id.Value));
        }
        public IdentityAuthorizationResult Facts() => new(IdentityAuthorizationOutcome.Covered, Actor, Tenant, _organization,
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

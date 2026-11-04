using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class IdentityOperationPolicyCombinationTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.CreateVersion7());
    private static readonly PermissionDefinition TenantAdmin = IdentityOperationPolicySource.MembershipAdministration;
    private static readonly PermissionDefinition OrganizationAdmin = new(PermissionScope.Organization, TenantAdmin.Key);
    private static readonly PermissionDefinition TenantProfile = new(PermissionScope.Tenant, "identity.membership_profile.write");
    private static readonly PermissionDefinition OrganizationProfile = new(PermissionScope.Organization, TenantProfile.Key);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Member_detail_chooses_the_actual_Tenant_or_exact_branch_clause_and_only_Tenant_mints_a_read(bool tenant)
    {
        var wide = new OperationPolicyClause([TenantAdmin], OperationReadScope.Tenant);
        var scoped = new OperationPolicyClause([OrganizationAdmin]);
        using var fixture = new Fixture([wide, scoped]);
        var member = Member(tenant ? null : Branch, tenant ? TenantAdmin : OrganizationAdmin);
        fixture.Facts(member);
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Allowed, decision.Outcome);
        Assert.Same(tenant ? wide : scoped, decision.MatchedClause);
        Assert.Equal(member.MembershipId, Assert.Single(decision.MembershipIds));
        Assert.Equal(member.Roles[0].RoleId, Assert.Single(decision.RoleIds));
        if (tenant)
        {
            Assert.NotNull(decision.ReadWitness);
            Assert.Same(wide, decision.ReadWitness.MatchedClause);
            Assert.Same(fixture.Invocation, decision.ReadWitness.Invocation);
        }
        else Assert.Null(decision.ReadWitness);
        Assert.Equal(OperationReadScope.Scoped, fixture.Policy.ReadScope);
        Assert.Null(fixture.Policy.RequiredPermission);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Organization_directory_keeps_both_alternatives_scoped_and_requires_a_branch(bool tenant)
    {
        using var fixture = new Fixture([new([TenantAdmin]), new([OrganizationAdmin])], requiresOrganization: true);
        fixture.Facts(Member(tenant ? null : Branch, tenant ? TenantAdmin : OrganizationAdmin));
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Allowed, decision.Outcome);
        Assert.Null(decision.ReadWitness);
        using var orgless = new Fixture([new([TenantAdmin]), new([OrganizationAdmin])], organization: null, requiresOrganization: true);
        orgless.Facts(Member(null, TenantAdmin));
        Assert.Equal(ProtectedOperationOutcome.Forbidden, (await orgless.AuthorizeAsync()).Outcome);
    }

    [Fact]
    public async Task Conjunction_requires_every_pair_and_can_combine_only_declared_eligible_sources()
    {
        var clause = new OperationPolicyClause([TenantProfile, OrganizationAdmin]);
        using var fixture = new Fixture([clause]);
        var tenant = Member(null, TenantProfile);
        var branch = Member(Branch, OrganizationAdmin);
        fixture.Facts(tenant, branch);
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Allowed, decision.Outcome);
        Assert.Same(clause, decision.MatchedClause);
        Assert.Equal(new[] { tenant.MembershipId, branch.MembershipId }.Order(), decision.MembershipIds);
        Assert.Equal(new[] { tenant.Roles[0].RoleId, branch.Roles[0].RoleId }.Order(), decision.RoleIds);
        Assert.Null(decision.ReadWitness);
        fixture.Facts(tenant);
        var denied = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Forbidden, denied.Outcome);
        Assert.Null(denied.MatchedClause);
        Assert.Empty(denied.MembershipIds);
        Assert.Empty(denied.RoleIds);
    }

    [Fact]
    public async Task Requirements_from_different_failed_alternatives_cannot_be_spliced_into_a_success()
    {
        using var fixture = new Fixture([new([TenantProfile, TenantAdmin]), new([OrganizationProfile, OrganizationAdmin])]);
        fixture.Facts(Member(null, TenantProfile), Member(Branch, OrganizationAdmin));
        Assert.Equal(ProtectedOperationOutcome.Forbidden, (await fixture.AuthorizeAsync()).Outcome);
    }

    [Fact]
    public async Task Foreign_branch_facts_never_contribute_and_orgless_never_consumes_an_Organization_pair()
    {
        using var fixture = new Fixture([new([TenantAdmin]), new([OrganizationAdmin])]);
        fixture.Facts(Member(OrganizationId.From(Guid.CreateVersion7()), OrganizationAdmin));
        var foreign = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Unavailable, foreign.Outcome);
        Assert.Empty(foreign.MembershipIds);
        Assert.Null(foreign.MatchedClause);
        using var orgless = new Fixture([new([OrganizationAdmin])], organization: null);
        orgless.Facts(Member(null, OrganizationAdmin));
        Assert.Equal(ProtectedOperationOutcome.Forbidden, (await orgless.AuthorizeAsync()).Outcome);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("virtual")]
    public async Task Every_conjunct_needs_its_exact_active_assignable_registration(string variant)
    {
        using var fixture = new Fixture([new([TenantProfile, TenantAdmin])]);
        fixture.Facts(Member(null, TenantProfile, TenantAdmin));
        fixture.Catalog.TryGetDefinition(TenantProfile.Scope, TenantProfile.Key, out Arg.Any<PermissionRegistration>()).Returns(call =>
        {
            call[2] = new PermissionRegistration(variant == "inactive"
                ? new(TenantProfile.Scope, TenantProfile.Key, isActive: false) : TenantProfile, variant != "virtual");
            return variant != "missing";
        });
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Forbidden, decision.Outcome);
        Assert.Empty(decision.MembershipIds);
        Assert.Null(decision.MatchedClause);
    }

    [Fact]
    public async Task A_failed_alternative_does_not_publish_its_contributors_or_prevent_an_independent_success()
    {
        var tenant = new OperationPolicyClause([TenantProfile, TenantAdmin]);
        var organization = new OperationPolicyClause([OrganizationAdmin]);
        using var fixture = new Fixture([tenant, organization]);
        var unused = Member(null, TenantProfile);
        var used = Member(Branch, OrganizationAdmin);
        fixture.Facts(unused, used);
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Allowed, decision.Outcome);
        Assert.Same(organization, decision.MatchedClause);
        Assert.Equal(used.MembershipId, Assert.Single(decision.MembershipIds));
        Assert.Equal(used.Roles[0].RoleId, Assert.Single(decision.RoleIds));
    }

    [Fact]
    public async Task Each_combination_check_reads_fresh_committed_facts_and_does_not_reuse_a_selected_clause()
    {
        using var fixture = new Fixture([new([TenantAdmin], OperationReadScope.Tenant), new([OrganizationAdmin])]);
        fixture.Facts(Member(null, TenantAdmin));
        Assert.NotNull((await fixture.AuthorizeAsync()).ReadWitness);
        fixture.Facts(Member(Branch, OrganizationAdmin));
        var changed = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Allowed, changed.Outcome);
        Assert.Same(fixture.Policy.Alternatives[1], changed.MatchedClause);
        Assert.Null(changed.ReadWitness);
        fixture.Facts(Member(Branch));
        Assert.Equal(ProtectedOperationOutcome.Forbidden, (await fixture.AuthorizeAsync()).Outcome);
        await fixture.Reader.Received(3).ReadAsync(fixture.Candidate, Arg.Any<CancellationToken>());
    }

    private static AuthorizationMembershipFacts Member(OrganizationId? organization, params PermissionDefinition[] grants) =>
        new(Guid.CreateVersion7(), organization, [new(Guid.CreateVersion7(), organization is null ? BuiltInRoleKey.TenantAdmin : BuiltInRoleKey.OrgAdmin,
            organization is null ? PermissionScope.Tenant : PermissionScope.Organization, [.. grants])]);

    private sealed record Operation;

    private sealed class Fixture : IDisposable
    {
        private readonly InvocationSource _invocations = new();
        private readonly OrganizationId? _organization;
        public Fixture(OperationPolicyClause[] alternatives, bool requiresOrganization = false)
            : this(alternatives, Branch, requiresOrganization) { }
        public Fixture(OperationPolicyClause[] alternatives, OrganizationId? organization, bool requiresOrganization = false)
        {
            _organization = organization;
            var context = Substitute.For<ITenantContext>();
            context.IsResolved.Returns(true);
            context.TenantId.Returns(Tenant);
            context.OrganizationId.Returns(organization);
            context.UserId.Returns(Actor);
            context.Origin.Returns(TenantContextOrigin.HostAndClaim);
            Candidate = Source.Mint(organization);
            Policy = OperationPolicy.ForProtected<Operation>("identity.test.combination", alternatives, requiresOrganization: requiresOrganization);
            Invocation = _invocations.Mint(new Operation(), Policy, context, Candidate);
            Catalog.TryGet(typeof(Operation), out Arg.Any<OperationPolicy>()).Returns(call => { call[1] = Policy; return true; });
            foreach (var permission in alternatives.SelectMany(clause => clause.RequiredPermissions).Distinct())
                Catalog.TryGetDefinition(permission.Scope, permission.Key, out Arg.Any<PermissionRegistration>()).Returns(call =>
                {
                    call[2] = new PermissionRegistration(permission, true);
                    return true;
                });
            Authorizer = new(() => Reader, Catalog);
        }
        public CandidateSource Source { get; } = new();
        public IIdentityAuthorizationReader Reader { get; } = Substitute.For<IIdentityAuthorizationReader>();
        public IOperationPolicyCatalog Catalog { get; } = Substitute.For<IOperationPolicyCatalog>();
        public AuthorizationCandidate Candidate { get; }
        public OperationPolicy Policy { get; }
        public OperationInvocation Invocation { get; }
        public IdentityOperationAuthorizer Authorizer { get; }
        public void Facts(params AuthorizationMembershipFacts[] members) => Reader.ReadAsync(Candidate, Arg.Any<CancellationToken>())
            .Returns(new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Covered, Actor, Tenant, _organization, [.. members]));
        public Task<ProtectedOperationDecision> AuthorizeAsync() => Authorizer.AuthorizeAsync(Invocation, Policy, Candidate);
        public void Dispose() => Source.Dispose();
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate Mint(OrganizationId? organization) => ForCandidate(AuthorizationSourceKind.Http, Actor, Tenant, organization);
    }

    private sealed class InvocationSource : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        public OperationInvocation Mint(object operation, OperationPolicy policy, ITenantContext context, AuthorizationCandidate candidate) =>
            _current = ForInvocation(operation, policy, context, candidate, null, CancellationToken.None);
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }
}

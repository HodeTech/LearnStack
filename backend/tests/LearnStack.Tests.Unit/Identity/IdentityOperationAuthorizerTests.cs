using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class IdentityOperationAuthorizerTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.CreateVersion7());
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Role = Guid.CreateVersion7();
    private static readonly PermissionDefinition TenantAdmin = IdentityOperationPolicySource.MembershipAdministration;

    [Fact]
    public async Task Branch_directory_requires_the_exact_Tenant_pair_and_mints_only_bounded_contributing_identifiers()
    {
        using var fixture = new Fixture();
        fixture.Facts(new AuthorizationMembershipFacts(Member, null, [new(Role, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, [TenantAdmin])]),
            new(Guid.CreateVersion7(), Branch, []));
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Allowed, decision.Outcome);
        Assert.Equal(Member, Assert.Single(decision.MembershipIds));
        Assert.Equal(Role, Assert.Single(decision.RoleIds));
        Assert.NotNull(decision.ReadWitness);
        Assert.Same(fixture.Invocation, decision.ReadWitness.Invocation);
        Assert.True(decision.IsActive);
    }

    [Fact]
    public async Task Same_key_Organization_grant_cannot_authorize_the_Tenant_directory()
    {
        using var fixture = new Fixture();
        fixture.Facts(new AuthorizationMembershipFacts(Member, Branch, [new(Role, BuiltInRoleKey.OrgAdmin, PermissionScope.Organization,
            [new(PermissionScope.Organization, TenantAdmin.Key)])]));
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Forbidden, decision.Outcome);
        Assert.Empty(decision.MembershipIds);
        Assert.Empty(decision.RoleIds);
        Assert.Null(decision.ReadWitness);
    }

    [Fact]
    public async Task Account_coverage_does_not_require_a_Role_or_grant_and_never_widens_reads()
    {
        using var fixture = new Fixture(account: true);
        fixture.Facts(new AuthorizationMembershipFacts(Member, Branch, []));
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Allowed, decision.Outcome);
        Assert.Equal(Member, Assert.Single(decision.MembershipIds));
        Assert.Empty(decision.RoleIds);
        Assert.Null(decision.ReadWitness);
    }

    [Theory]
    [InlineData(IdentityAuthorizationOutcome.NotCovered, ProtectedOperationOutcome.NotCovered)]
    [InlineData(IdentityAuthorizationOutcome.Unavailable, ProtectedOperationOutcome.Unavailable)]
    public async Task Noncoverage_and_storage_failure_remain_distinct_without_identifiers(
        IdentityAuthorizationOutcome read, ProtectedOperationOutcome expected)
    {
        using var fixture = new Fixture();
        fixture.Reader.ReadAsync(fixture.Candidate, Arg.Any<CancellationToken>()).Returns(new IdentityAuthorizationResult(read, Actor, Tenant, Branch, []));
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(expected, decision.Outcome);
        Assert.Empty(decision.MembershipIds);
        Assert.Empty(decision.RoleIds);
    }

    [Fact]
    public async Task Every_act_reads_again_so_a_committed_revocation_is_not_reused()
    {
        using var fixture = new Fixture();
        var allowed = new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Covered, Actor, Tenant, Branch,
            [new(Member, null, [new(Role, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, [TenantAdmin])])]);
        var denied = allowed with { Memberships = [new(Member, null, [])] };
        fixture.Reader.ReadAsync(fixture.Candidate, Arg.Any<CancellationToken>()).Returns(allowed, denied);
        Assert.Equal(ProtectedOperationOutcome.Allowed, (await fixture.AuthorizeAsync()).Outcome);
        Assert.Equal(ProtectedOperationOutcome.Forbidden, (await fixture.AuthorizeAsync()).Outcome);
        await fixture.Reader.Received(2).ReadAsync(fixture.Candidate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Equivalent_but_unregistered_policy_and_foreign_candidate_are_refused_before_read()
    {
        using var fixture = new Fixture();
        var clone = OperationPolicy.ForProtected<Operation>("identity.test.read", TenantAdmin, OperationReadScope.Tenant);
        Assert.Equal(ProtectedOperationOutcome.NotCovered,
            (await fixture.Authorizer.AuthorizeAsync(fixture.Invocation, clone, fixture.Candidate)).Outcome);
        using var foreignSource = new CandidateSource();
        var foreign = foreignSource.Mint();
        var refusal = await fixture.Authorizer.AuthorizeAsync(fixture.Invocation, fixture.Policy, foreign);
        Assert.Equal(ProtectedOperationOutcome.NotCovered, refusal.Outcome);
        Assert.Same(fixture.Invocation, refusal.Invocation);
        await fixture.Reader.DidNotReceiveWithAnyArgs().ReadAsync(null!);
    }

    [Fact]
    public async Task Context_retargeting_and_foreign_result_scope_refuse_without_publishing_identifiers()
    {
        using var fixture = new Fixture();
        fixture.Context.OrganizationId.Returns(OrganizationId.From(Guid.CreateVersion7()));
        Assert.Equal(ProtectedOperationOutcome.Unavailable, (await fixture.AuthorizeAsync()).Outcome);
        await fixture.Reader.DidNotReceiveWithAnyArgs().ReadAsync(null!);
        fixture.Context.OrganizationId.Returns(Branch);
        fixture.Reader.ReadAsync(fixture.Candidate, Arg.Any<CancellationToken>()).Returns(
            new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Covered, Actor, TenantId.From(Guid.CreateVersion7()), Branch, []));
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.NotCovered, decision.Outcome);
        Assert.Empty(decision.MembershipIds);
    }

    [Fact]
    public async Task Candidate_expiry_during_the_fresh_read_is_unavailable_after_the_reader_finishes()
    {
        using var fixture = new Fixture();
        fixture.Reader.ReadAsync(fixture.Candidate, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fixture.Source.Dispose();
            return new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Covered, Actor, Tenant, Branch,
                [new(Member, null, [new(Role, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, [TenantAdmin])])]);
        });
        var decision = await fixture.AuthorizeAsync();
        Assert.Equal(ProtectedOperationOutcome.Unavailable, decision.Outcome);
        Assert.Empty(decision.MembershipIds);
        Assert.Null(decision.ReadWitness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_or_inactive_definition_never_authorizes_even_if_returned_as_a_grant(bool inactive)
    {
        var permission = new PermissionDefinition(PermissionScope.Tenant, "identity.other.admin", isActive: !inactive);
        using var fixture = new Fixture(permission: permission, registerPermission: inactive);
        fixture.Facts(new AuthorizationMembershipFacts(Member, null, [new(Role, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, [permission])]));
        Assert.Equal(ProtectedOperationOutcome.Forbidden, (await fixture.AuthorizeAsync()).Outcome);
    }

    [Fact]
    public async Task Organization_permission_uses_only_the_requested_branch_and_orgless_write_still_refuses()
    {
        var permission = new PermissionDefinition(PermissionScope.Organization, "identity.membership_profile.write");
        using var fixture = new Fixture(permission: permission, scope: OperationReadScope.Scoped);
        fixture.Facts(new AuthorizationMembershipFacts(Member, Branch, [new(Role, BuiltInRoleKey.Learner, PermissionScope.Organization, [permission])]));
        Assert.Equal(ProtectedOperationOutcome.Allowed, (await fixture.AuthorizeAsync()).Outcome);
        fixture.Facts(new AuthorizationMembershipFacts(Member, OrganizationId.From(Guid.CreateVersion7()), [new(Role, BuiltInRoleKey.Learner, PermissionScope.Organization, [permission])]));
        Assert.Equal(ProtectedOperationOutcome.Unavailable, (await fixture.AuthorizeAsync()).Outcome);
        using var write = new Fixture(scope: OperationReadScope.Scoped, orgless: true);
        write.Facts(new AuthorizationMembershipFacts(Member, null, [new(Role, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, [TenantAdmin])]));
        Assert.Equal(ProtectedOperationOutcome.Forbidden, (await write.AuthorizeAsync()).Outcome);
    }

    [Fact]
    public async Task Request_cancellation_propagates_before_and_after_the_live_read()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Reader.ReadAsync(fixture.Candidate, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return new IdentityAuthorizationResult(IdentityAuthorizationOutcome.NotCovered, Actor, Tenant, Branch, []);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Authorizer.AuthorizeAsync(
            fixture.Invocation, fixture.Policy, fixture.Candidate, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Authorizer.AuthorizeAsync(
            fixture.Invocation, fixture.Policy, fixture.Candidate, cancellation.Token));
        await fixture.Reader.Received(1).ReadAsync(fixture.Candidate, Arg.Any<CancellationToken>());
    }

    private sealed record Operation;

    private sealed class Fixture : IDisposable
    {
        private readonly InvocationSource _invocations = new();
        public Fixture(bool account = false, PermissionDefinition? permission = null, bool registerPermission = true,
            OperationReadScope scope = OperationReadScope.Tenant, bool orgless = false)
        {
            Context = Substitute.For<ITenantContext>();
            Context.IsResolved.Returns(true);
            Context.TenantId.Returns(Tenant);
            Context.OrganizationId.Returns(Branch);
            Context.UserId.Returns(Actor);
            Context.Origin.Returns(TenantContextOrigin.HostAndClaim);
            Candidate = Source.Mint();
            Policy = account ? OperationPolicy.ForAccount<Operation>("identity.test.account")
                : OperationPolicy.ForProtected<Operation>("identity.test.read", permission ?? TenantAdmin, scope, orgless);
            Invocation = _invocations.Mint(new Operation(), Policy, Context, Candidate);
            var catalog = Substitute.For<IOperationPolicyCatalog>();
            catalog.TryGet(typeof(Operation), out Arg.Any<OperationPolicy>()).Returns(call => { call[1] = Policy; return true; });
            var definition = permission ?? TenantAdmin;
            catalog.TryGetDefinition(definition.Scope, definition.Key, out Arg.Any<PermissionRegistration>()).Returns(call =>
            {
                call[2] = new PermissionRegistration(definition, true);
                return registerPermission;
            });
            Authorizer = new IdentityOperationAuthorizer(Reader, catalog);
        }
        public CandidateSource Source { get; } = new();
        public IIdentityAuthorizationReader Reader { get; } = Substitute.For<IIdentityAuthorizationReader>();
        public ITenantContext Context { get; }
        public AuthorizationCandidate Candidate { get; }
        public OperationPolicy Policy { get; }
        public OperationInvocation Invocation { get; }
        public IdentityOperationAuthorizer Authorizer { get; }
        public void Facts(params AuthorizationMembershipFacts[] members) => Reader.ReadAsync(Candidate, Arg.Any<CancellationToken>())
            .Returns(new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Covered, Actor, Tenant, Branch, [.. members]));
        public Task<ProtectedOperationDecision> AuthorizeAsync() => Authorizer.AuthorizeAsync(Invocation, Policy, Candidate);
        public void Dispose() => Source.Dispose();
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate Mint() => ForCandidate(AuthorizationSourceKind.Http, Actor, Tenant, Branch);
    }

    private sealed class InvocationSource : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        public OperationInvocation Mint(object operation, OperationPolicy policy, ITenantContext context, AuthorizationCandidate candidate)
        {
            _current = ForInvocation(operation, policy, context, candidate, null, CancellationToken.None);
            return _current;
        }
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }
}

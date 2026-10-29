using System.Reflection;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Authorization;

public sealed class OperationClauseWitnessTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.CreateVersion7());
    private static readonly PermissionDefinition TenantPair = new(PermissionScope.Tenant, "identity.membership.admin");
    private static readonly PermissionDefinition OrganizationPair = new(PermissionScope.Organization, TenantPair.Key);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Coordinator_consumes_only_the_chosen_clauses_witness_and_closes_its_invocation(int choice)
    {
        var policy = Policy();
        var authorizer = new Authorizer { Choice = choice };
        using var source = new CandidateSource();
        using var coordinator = Owner(policy, authorizer, source);
        OperationReadWitness? read = null;
        var result = await coordinator.ExecuteRequestAsync<Operation, Result<None>>(new(), () =>
        {
            Assert.Same(policy.Alternatives[choice], coordinator.CurrentDecision!.MatchedClause);
            read = coordinator.CurrentReadWitness;
            if (choice == 0)
            {
                Assert.NotNull(read);
                Assert.Same(policy.Alternatives[0], read.MatchedClause);
            }
            else Assert.Null(read);
            return Success();
        });
        Assert.True(result.IsSuccess);
        Assert.False(authorizer.Decision!.IsActive);
        Assert.Null(coordinator.CurrentReadWitness);
        if (read is not null) Assert.False(read.IsActive);
    }

    [Theory]
    [InlineData("missing-clause")]
    [InlineData("foreign-clause")]
    [InlineData("wide-without-witness")]
    [InlineData("scoped-with-wide-witness")]
    [InlineData("wide-with-other-witness")]
    public async Task Coordinator_independently_refuses_counterfeit_clause_or_read_authority(string mutation)
    {
        using var source = new CandidateSource();
        using var coordinator = Owner(Policy(), new Authorizer { Mutation = mutation }, source);
        var result = await coordinator.ExecuteRequestAsync<Operation, Result<None>>(new(), Never);
        Assert.Equal("dependency_unavailable", result.Error!.Code);
        Assert.Null(coordinator.CurrentDecision);
        Assert.Null(coordinator.CurrentReadWitness);
    }

    [Theory]
    [InlineData("omit-choice")]
    [InlineData("mint-foreign-clause")]
    public async Task Decision_minter_requires_an_actual_canonical_clause_for_a_combination(string mutation)
    {
        using var source = new CandidateSource();
        using var coordinator = Owner(Policy(), new Authorizer { Mutation = mutation }, source);
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.ExecuteRequestAsync<Operation, Result<None>>(new(), Never));
        Assert.Null(coordinator.CurrentInvocation);
    }

    [Fact]
    public async Task Legacy_single_permission_minter_derives_only_its_sole_canonical_clause()
    {
        var policy = OperationPolicy.ForProtected<Operation>("identity.test.single", TenantPair, OperationReadScope.Tenant);
        using var source = new CandidateSource();
        using var coordinator = Owner(policy, new Authorizer { Mutation = "omit-choice" }, source);
        var result = await coordinator.ExecuteRequestAsync<Operation, Result<None>>(new(), () =>
        {
            Assert.Same(policy.Alternatives[0], coordinator.CurrentDecision!.MatchedClause);
            Assert.Same(policy.Alternatives[0], coordinator.CurrentReadWitness!.MatchedClause);
            return Success();
        });
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Branch_only_operation_refuses_an_orgless_Tenant_actor_before_any_authorizer_call()
    {
        var policy = OperationPolicy.ForProtected<Operation>("identity.test.branch", [new([TenantPair]), new([OrganizationPair])],
            requiresOrganization: true);
        var authorizer = new Authorizer();
        using var source = new CandidateSource();
        using var coordinator = Owner(policy, authorizer, source, organization: null);
        var result = await coordinator.ExecuteRequestAsync<Operation, Result<None>>(new(), Never);
        Assert.Equal("forbidden", result.Error!.Code);
        Assert.Equal(0, authorizer.Calls);
    }

    [Fact]
    public async Task Orgless_write_constraint_independently_refuses_an_allowed_scoped_decision_in_a_branch()
    {
        var policy = OperationPolicy.ForProtected<Operation>("identity.test.write", [new([TenantPair]), new([OrganizationPair])],
            requiresOrglessWrite: true);
        using var source = new CandidateSource();
        using var coordinator = Owner(policy, new Authorizer(), source);
        var result = await coordinator.ExecuteRequestAsync<Operation, Result<None>>(new(), Never);
        Assert.Equal("forbidden", result.Error!.Code);
    }

    private static OperationPolicy Policy() => OperationPolicy.ForProtected<Operation>("identity.test.combination",
        [new([TenantPair], OperationReadScope.Tenant), new([OrganizationPair])]);

    private static OperationAuthorizationCoordinator Owner(OperationPolicy policy, Authorizer authorizer, CandidateSource source) =>
        Owner(policy, authorizer, source, Branch);

    private static OperationAuthorizationCoordinator Owner(OperationPolicy policy, Authorizer authorizer, CandidateSource source,
        OrganizationId? organization)
    {
        var context = TenantContextFactory.Create(new()
        {
            HasValidatedPrincipal = true,
            ClaimTenantId = Tenant,
            ClaimOrganizationId = organization,
            UserId = Actor,
            MembershipCovers = true,
        }).Value!;
        OperationAuthorizationCoordinator? owner = null;
        var evidence = new AuthorizationEvidenceCoordinator(Substitute.For<IAuditStore>(), new AuditStateCapture(),
            Substitute.For<IUnitOfWork>(), new SystemClock(), new SystemGuidFactory(), () => owner!, [],
            NullLogger<AuthorizationEvidenceCoordinator>.Instance);
        return owner = new(new OperationPolicyCatalog([new Source(policy)]), authorizer, context, [source], [], evidence);
    }

    private static Task<Result<None>> Success() => Task.FromResult(Result.Ok(None.Value));
    private static Task<Result<None>> Never() => throw new InvalidOperationException("Handler must remain undispatched.");
    private sealed record Operation;

    private sealed class Source(OperationPolicy policy) : IOperationPolicySource
    {
        public IEnumerable<PermissionRegistration> Definitions => [new(TenantPair, true), new(OrganizationPair, true)];
        public IEnumerable<OperationPolicy> Policies => [policy];
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        public AuthorizationCandidate ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            ForCandidate(AuthorizationSourceKind.Http, Actor, tenant, organization);
    }

    private sealed class Authorizer : ProtectedOperationDecision.MintingSource, IProtectedOperationAuthorizer
    {
        public int Choice { get; init; }
        public string? Mutation { get; init; }
        public int Calls { get; private set; }
        public ProtectedOperationDecision? Decision { get; private set; }
        public Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
            AuthorizationCandidate candidate, CancellationToken cancellationToken = default)
        {
            Calls++;
            var chosen = policy.Alternatives[Choice];
            var members = new[] { Guid.CreateVersion7() };
            if (Mutation == "omit-choice")
                return Task.FromResult(ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, members));
            if (Mutation == "mint-foreign-clause")
                return Task.FromResult(ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, members,
                    matchedClause: new(chosen.RequiredPermissions, chosen.ReadScope)));
            var decision = ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, members, matchedClause: chosen);
            Decision = decision;
            if (Mutation is null) return Task.FromResult(decision);
            var wide = ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, members, matchedClause: policy.Alternatives[0]);
            OperationPolicyClause? clause = Mutation switch
            {
                "missing-clause" => null,
                "foreign-clause" => new(chosen.RequiredPermissions, chosen.ReadScope),
                "scoped-with-wide-witness" => policy.Alternatives[1],
                _ => chosen,
            };
            var witness = Mutation == "wide-without-witness" ? null : wide.ReadWitness;
            if (Mutation == "wide-with-other-witness")
            {
                var invocationSource = new OtherInvocationSource();
                var otherInvocation = invocationSource.Mint(new Operation(), policy, invocation.Context, candidate);
                witness = ForDecision(otherInvocation, candidate, ProtectedOperationOutcome.Allowed, members,
                    matchedClause: policy.Alternatives[0]).ReadWitness;
            }
            // Deliberately bypass the private minter to prove the coordinator's independent boundary.
            var constructor = typeof(ProtectedOperationDecision).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            return Task.FromResult((ProtectedOperationDecision)constructor.Invoke(
                [invocation, ProtectedOperationOutcome.Allowed, decision.MembershipIds, decision.RoleIds, clause, witness]));
        }
    }

    private sealed class OtherInvocationSource : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        public OperationInvocation Mint(object operation, OperationPolicy policy, ITenantContext context, AuthorizationCandidate candidate) =>
            _current = ForInvocation(operation, policy, context, candidate, null, CancellationToken.None);
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }
}

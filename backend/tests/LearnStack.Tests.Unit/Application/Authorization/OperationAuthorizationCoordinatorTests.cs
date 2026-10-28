using System.Data.Common;
using LearnStack.Application.Authorization;
using LearnStack.Application.Audit;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;
using MediatR;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Authorization;

public sealed class OperationAuthorizationCoordinatorTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.CreateVersion7());
    private static readonly PermissionDefinition Pair = new(PermissionScope.Tenant, "identity.example.read");

    [Fact]
    public async Task Missing_classification_denies_before_authorizer_or_handler()
    {
        using var source = new CandidateSource();
        var authorizer = new Authorizer();
        using var owner = Owner(Context(), authorizer, [source]);
        var result = await owner.ExecuteRequestAsync<Unknown, Result<None>>(new(), Never);
        Assert.Equal("forbidden", result.Error!.Code);
        Assert.Equal(0, authorizer.Calls);
    }

    [Fact]
    public async Task Public_host_reads_use_no_candidate_or_membership_evaluation()
    {
        var context = TenantContextFactory.Create(new() { HostTenantId = Tenant, HostOrganizationId = Branch }).Value!;
        var authorizer = new Authorizer();
        using var owner = Owner(context, authorizer, [new ForbiddenCandidateSource()]);
        var result = await owner.ExecuteRequestAsync<Public, Result<None>>(new(), Success);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, authorizer.Calls);
    }

    [Fact]
    public async Task Claim_only_context_never_inherits_AccountSite_host_authority()
    {
        using var source = new CandidateSource();
        var authorizer = new Authorizer();
        using var owner = Owner(Context(), authorizer, [source]);
        var result = await owner.ExecuteRequestAsync<Account, Result<None>>(new(), Never);
        Assert.Equal("not_found", result.Error!.Code);
        Assert.Equal(0, authorizer.Calls);
    }

    [Theory]
    [InlineData(ProtectedOperationOutcome.NotCovered, "not_found")]
    [InlineData(ProtectedOperationOutcome.Forbidden, "forbidden")]
    [InlineData(ProtectedOperationOutcome.Unavailable, "dependency_unavailable")]
    public async Task Coverage_capability_and_storage_refusals_remain_distinct(
        ProtectedOperationOutcome outcome, string code)
    {
        using var source = new CandidateSource();
        var authorizer = new Authorizer { Outcome = outcome };
        using var owner = Owner(Context(), authorizer, [source]);
        var result = await owner.ExecuteRequestAsync<Wide, Result<None>>(new(), Never);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(1, authorizer.Calls);
        Assert.Null(owner.CurrentInvocation);
        Assert.Null(owner.CurrentReadWitness);
    }

    [Fact]
    public async Task Every_actual_act_including_the_same_request_object_gets_a_new_invocation_and_evaluation()
    {
        using var source = new CandidateSource();
        var authorizer = new Authorizer();
        using var owner = Owner(Context(), authorizer, [source]);
        var request = new Wide();
        var first = await owner.ExecuteRequestAsync<Wide, Result<None>>(request, Success);
        var previous = authorizer.LastInvocation!;
        authorizer.Outcome = ProtectedOperationOutcome.Forbidden;
        var second = await owner.ExecuteRequestAsync<Wide, Result<None>>(request, Never);
        Assert.True(first.IsSuccess);
        Assert.Equal("forbidden", second.Error!.Code);
        Assert.Equal(2, authorizer.Calls);
        Assert.NotSame(previous, authorizer.LastInvocation);
        Assert.False(previous.IsActive);
    }

    [Fact]
    public async Task Same_scope_nested_invocations_preserve_live_ancestors_and_close_only_their_own_witness()
    {
        using var source = new CandidateSource();
        using var owner = Owner(Context(), new Authorizer(), [source]);
        OperationReadWitness? outer = null;
        OperationReadWitness? child = null;
        var result = await owner.ExecuteRequestAsync<Wide, Result<None>>(new(), async () =>
        {
            outer = owner.CurrentReadWitness;
            Assert.NotNull(outer);
            var nested = await owner.ExecuteRequestAsync<Wide, Result<None>>(new(), () =>
            {
                child = owner.CurrentReadWitness;
                Assert.NotNull(child);
                Assert.NotSame(outer, child);
                Assert.True(outer.IsActive);
                Assert.True(child.IsActive);
                return Success();
            });
            Assert.True(nested.IsSuccess);
            Assert.False(child!.IsActive);
            Assert.True(outer.IsActive);
            Assert.Same(outer, owner.CurrentReadWitness);
            return await Success();
        });
        Assert.True(result.IsSuccess);
        Assert.False(outer!.IsActive);
        Assert.False(child!.IsActive);
    }

    [Fact]
    public async Task Witness_binds_once_to_one_physical_unit_transaction_generation_and_expires_after_exit()
    {
        using var source = new CandidateSource();
        using var owner = Owner(Context(), new Authorizer(), [source]);
        var unit = Substitute.For<IUnitOfWork>();
        var other = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<DbTransaction>();
        OperationReadWitness? captured = null;
        await owner.ExecuteRequestAsync<Wide, Result<None>>(new(), () =>
        {
            captured = owner.CurrentReadWitness!;
            Assert.True(captured.TryBindTo(unit, transaction, 1));
            Assert.True(captured.IsBoundTo(unit, transaction, 1));
            Assert.False(captured.TryBindTo(unit, transaction, 1));
            Assert.False(captured.TryBindTo(other, transaction, 1));
            Assert.False(captured.IsBoundTo(other, transaction, 1));
            Assert.False(captured.IsBoundTo(unit, transaction, 2));
            return Success();
        });
        Assert.False(captured!.IsBoundTo(unit, transaction, 1));
    }

    [Fact]
    public async Task Inherited_task_cannot_revive_a_finished_invocation()
    {
        using var source = new CandidateSource();
        using var owner = Owner(Context(), new Authorizer(), [source]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? inherited = null;
        await owner.ExecuteRequestAsync<Wide, Result<None>>(new(), () =>
        {
            var witness = owner.CurrentReadWitness!;
            inherited = Task.Run(async () =>
            {
                await release.Task;
                return witness.IsActive || owner.CurrentInvocation is not null;
            });
            return Success();
        });
        release.SetResult();
        Assert.False(await inherited!);
    }

    [Fact]
    public async Task Ambiguous_human_sources_refuse_without_an_authority_read()
    {
        using var first = new CandidateSource();
        using var second = new CandidateSource();
        var authorizer = new Authorizer();
        using var owner = Owner(Context(), authorizer, [first, second]);
        Assert.Equal("not_found", (await owner.ExecuteRequestAsync<Wide, Result<None>>(new(), Never)).Error!.Code);
        Assert.Equal(0, authorizer.Calls);
    }

    [Fact]
    public async Task Finite_source_requires_exact_request_context_kind_and_open_lifetime()
    {
        var context = Context();
        using var source = new TrustedSource(context);
        using var owner = Owner(context, new Authorizer(), [], [source]);
        Assert.True((await owner.ExecuteRequestAsync<Seed, Result<None>>(new(), Success)).IsSuccess);
        source.Dispose();
        Assert.Equal("forbidden", (await owner.ExecuteRequestAsync<Seed, Result<None>>(new(), Never)).Error!.Code);
        Assert.Equal("not_found", (await owner.ExecuteRequestAsync<Wide, Result<None>>(new(), Never)).Error!.Code);
    }

    [Fact]
    public async Task Disposed_coordinator_and_canceled_request_never_start_an_act()
    {
        using var source = new CandidateSource();
        var authorizer = new Authorizer();
        using var owner = Owner(Context(), authorizer, [source]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.ExecuteRequestAsync<Wide, Result<None>>(new(), Never, cancellation.Token));
        owner.Dispose();
        Assert.Equal("forbidden", (await owner.ExecuteRequestAsync<Wide, Result<None>>(new(), Never)).Error!.Code);
        Assert.Equal(0, authorizer.Calls);
    }

    private static OperationAuthorizationCoordinator Owner(ITenantContext context, Authorizer authorizer,
        IAuthorizationCandidateSource[] candidates, ITrustedOperationSource[]? trusted = null)
    {
        OperationAuthorizationCoordinator? owner = null;
        var evidence = new AuthorizationEvidenceCoordinator(Substitute.For<IAuditStore>(), new AuditStateCapture(),
            Substitute.For<IUnitOfWork>(), new SystemClock(), new SystemGuidFactory(), () => owner!, [],
            NullLogger<AuthorizationEvidenceCoordinator>.Instance);
        return owner = new(new OperationPolicyCatalog([new TestPolicySource()]), authorizer, context, candidates, trusted ?? [], evidence);
    }

    private static TenantContext Context() => TenantContextFactory.Create(new()
    {
        HasValidatedPrincipal = true,
        ClaimTenantId = Tenant,
        ClaimOrganizationId = Branch,
        UserId = Actor,
        MembershipCovers = true,
    }).Value!;

    private static Task<Result<None>> Success() => Task.FromResult(Result.Ok(None.Value));
    private static Task<Result<None>> Never() => throw new InvalidOperationException("Handler must remain undispatched.");
    private sealed record Wide : IRequest<Result<None>>;
    private sealed record Public : IRequest<Result<None>>;
    private sealed record Account : IRequest<Result<None>>;
    private sealed record Seed : IRequest<Result<None>>;
    private sealed record Unknown : IRequest<Result<None>>;

    private sealed class TestPolicySource : IOperationPolicySource
    {
        private static readonly PermissionDefinition PublicPair = new(PermissionScope.Tenant, "tenancy.example.read");
        public IEnumerable<PermissionRegistration> Definitions => [new(Pair, true), new(PublicPair, false)];
        public IEnumerable<OperationPolicy> Policies =>
        [
            OperationPolicy.ForProtected<Wide>("identity.example.wide", Pair, OperationReadScope.Tenant),
            OperationPolicy.ForPublic<Public>("tenancy.example.read", PublicPair),
            OperationPolicy.ForAccount<Account>("tenancy.example.account"),
            OperationPolicy.ForTrusted<Seed>("tenancy.example.seed", AuthorizationSourceKind.Seed),
        ];
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        public AuthorizationCandidate ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            ForCandidate(AuthorizationSourceKind.Http, Actor, tenant, organization);
    }

    private sealed class ForbiddenCandidateSource : IAuthorizationCandidateSource
    {
        public AuthorizationCandidate? ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            throw new InvalidOperationException("Public execution must not inspect human authority.");
    }

    private sealed class Authorizer : ProtectedOperationDecision.MintingSource, IProtectedOperationAuthorizer
    {
        public int Calls { get; private set; }
        public ProtectedOperationOutcome Outcome { get; set; } = ProtectedOperationOutcome.Allowed;
        public OperationInvocation? LastInvocation { get; private set; }
        public Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
            AuthorizationCandidate candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastInvocation = invocation;
            return Task.FromResult(ForDecision(invocation, candidate, Outcome,
                Outcome == ProtectedOperationOutcome.Allowed ? [Guid.CreateVersion7()] : []));
        }
    }

    private sealed class TrustedSource(ITenantContext context) : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
    {
        public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext current) =>
            CanIssue && ReferenceEquals(current, context) && requestType == typeof(Seed)
                ? ForOperation(AuthorizationSourceKind.Seed, requestType, context) : null;
        protected override bool IsEvidenceCurrent(object? evidence) => true;
    }
}

using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// ADR-0106 § 4 and § 9: <see cref="OperationPolicyKind.OwnDataRequest"/> is the subject's Active Membership in
/// exactly the announced group through a live session, and <see cref="OperationPolicyKind.OwnDataRequestEntry"/> is
/// the retained own scope, admitted only by its witness and only under its own origin. Both are driven through the
/// real coordinator and the real Identity evaluator; the reader and the witness source are the only doubles.
/// </summary>
public sealed class OwnDataRequestPolicyTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.CreateVersion7());
    private static readonly OrganizationId OtherBranch = OrganizationId.From(Guid.CreateVersion7());
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly GlobalIdentitySubject Subject = new(IdentityAuthorityId.From(Guid.CreateVersion7()),
        "https://data-request.invalid/realms/learnstack", "data-request-subject");

    // ---- OwnDataRequest: the live-session kind ------------------------------------------------------------

    [Theory]
    [InlineData("branch-member-in-branch", "allowed")]
    [InlineData("tenant-member-in-tenant", "allowed")]
    [InlineData("tenant-member-in-branch", "lockey_not_found")]
    [InlineData("no-active-membership", "lockey_not_found")]
    [InlineData("foreign-branch-fact", "lockey_dependency_unavailable")]
    public async Task Own_data_request_needs_an_active_membership_in_exactly_the_announced_group(string label, string outcome)
    {
        var (context, member) = label switch
        {
            "branch-member-in-branch" => (Branch, (OrganizationId?)Branch),
            "tenant-member-in-tenant" => ((OrganizationId?)null, (OrganizationId?)null),
            "tenant-member-in-branch" => (Branch, (OrganizationId?)null),
            "foreign-branch-fact" => (Branch, OtherBranch),
            _ => (Branch, (OrganizationId?)Branch),
        };
        using var rig = new Rig(TenantContextOrigin.HostAndClaim, context);
        // The committed reader returns only Active, undeleted Memberships covering the announced scope: a Suspended or
        // Revoked person, or one with no Membership at all, reaches the evaluator as NotCovered.
        rig.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>()).Returns(label == "no-active-membership"
            ? new IdentityAuthorizationResult(IdentityAuthorizationOutcome.NotCovered, Actor, Tenant, context, [])
            : new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Covered, Actor, Tenant, context,
                [new AuthorizationMembershipFacts(Member, member, [])]));

        var result = await rig.RunAsync(new CreateOwnDataExportCommand());

        if (outcome == "allowed")
        {
            result.IsSuccess.Should().BeTrue();
            rig.Decision!.MembershipIds.Should().Equal(Member);
            rig.Decision.DataRequestWitness.Should().BeNull();
        }
        else if (outcome == "lockey_not_found")
        {
            result.IsFailure.Should().BeTrue();
            result.Error.Should().BeSameAs(TenantContextFactory.Refused,
                "a person outside the group is not covered: the not-found shape, never a forbidden that names the group");
        }
        else
        {
            // The committed reader never returns a Membership outside the announced scope; one that does is an
            // inconsistent read, which fails closed as unavailable rather than as a coverage answer.
            result.Error!.Message.Key.Should().Be(outcome);
        }
    }

    [Fact]
    public async Task Own_data_request_storage_failure_is_unavailable_not_a_refusal()
    {
        using var rig = new Rig(TenantContextOrigin.HostAndClaim, Branch);
        rig.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Unavailable, Actor, Tenant, Branch, []));

        var result = await rig.RunAsync(new CreateOwnDataExportCommand());

        result.Error!.Message.Key.Should().Be("lockey_dependency_unavailable");
    }

    [Theory]
    [InlineData("retained-origin")]
    [InlineData("seed-candidate")]
    [InlineData("foreign-policy-id")]
    public async Task Own_data_request_refuses_the_retained_origin_a_non_http_candidate_and_a_foreign_operation(string label)
    {
        using var rig = new Rig(label == "retained-origin" ? TenantContextOrigin.HostAndOwnDataRequestScope : TenantContextOrigin.HostAndClaim,
            Branch, candidateKind: label == "seed-candidate" ? AuthorizationSourceKind.Seed : AuthorizationSourceKind.Http);
        rig.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Covered, Actor, Tenant, Branch,
                [new AuthorizationMembershipFacts(Member, Branch, [])]));

        var result = label == "foreign-policy-id" ? await rig.RunAsync(new ForeignOwnProbe()) : await rig.RunAsync(new CreateOwnDataExportCommand());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeSameAs(TenantContextFactory.Refused);
        if (label == "retained-origin")
            await rig.Reader.DidNotReceive().ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The arm is pinned to the ten live request types and their own ids: a type outside the set under a well-formed
    /// <c>identity.data_request.</c> id, and a live type under another id, are not covered although the Membership is.
    /// </summary>
    [Theory]
    [InlineData("probe-type-prefixed-id")]
    [InlineData("live-type-foreign-id")]
    public async Task Own_data_request_admits_only_its_ten_live_types_under_their_own_ids(string label)
    {
        using var rig = new Rig(TenantContextOrigin.HostAndClaim, Branch);
        rig.Reader.ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityAuthorizationResult(IdentityAuthorizationOutcome.Covered, Actor, Tenant, Branch,
                [new AuthorizationMembershipFacts(Member, Branch, [])]));

        var result = label == "probe-type-prefixed-id"
            ? await rig.RunAsync(new CreateProbe())
            : await rig.RunAsync(new ListOwnDataRequestsQuery(null, 20));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeSameAs(TenantContextFactory.Refused);
        new IdentityOperationPolicySource().Policies.Where(policy => policy.Kind == OperationPolicyKind.OwnDataRequest)
            .Should().HaveCount(10, "the production catalog registers exactly the pinned set");
        (await rig.RunAsync(new CreateOwnDataExportCommand())).IsSuccess.Should().BeTrue("the pinned type under its own id is admitted");
    }

    // ---- OwnDataRequestEntry: the retained own scope ------------------------------------------------------

    [Theory]
    [InlineData("tenant-wide-group")]
    [InlineData("organization-group")]
    public async Task Entry_is_admitted_by_its_witness_alone_without_reading_a_membership(string label)
    {
        var group = new DataRequestScopeGroup(Tenant, label == "organization-group" ? Branch : null);
        using var rig = new Rig(TenantContextOrigin.HostAndOwnDataRequestScope, group.OrganizationId, withWitness: true);
        rig.Witnesses.Purpose = OwnDataRequestPurpose.Read;
        rig.Witnesses.Group = group;

        var result = await rig.RunAsync(new ReadProbe());

        result.IsSuccess.Should().BeTrue("a Suspended person and one whose Membership is gone are served by the witness");
        rig.Decision!.DataRequestWitness.Should().NotBeNull();
        rig.Decision.MembershipIds.Should().BeEmpty("the retained scope supplies no Membership authority");
        rig.Decision.DataRequestWitness!.Group.Should().Be(group);
        await rig.Reader.DidNotReceive().ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("no-source")]
    [InlineData("closed-source")]
    [InlineData("two-sources")]
    [InlineData("live-session-origin")]
    [InlineData("seed-candidate")]
    public async Task Entry_without_exactly_one_live_witness_in_its_own_origin_is_not_found(string label)
    {
        using var rig = new Rig(label == "live-session-origin" ? TenantContextOrigin.HostAndClaim : TenantContextOrigin.HostAndOwnDataRequestScope,
            Branch, withWitness: label != "no-source", twoSources: label == "two-sources",
            candidateKind: label == "seed-candidate" ? AuthorizationSourceKind.Seed : AuthorizationSourceKind.Http);
        rig.Witnesses.Purpose = OwnDataRequestPurpose.Read;
        rig.Witnesses.Group = new DataRequestScopeGroup(Tenant, Branch);
        if (label == "closed-source") rig.Witnesses.Dispose();

        var result = await rig.RunAsync(new ReadProbe());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeSameAs(TenantContextFactory.Refused);
        await rig.Reader.DidNotReceive().ReadAsync(Arg.Any<AuthorizationCandidate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_entry_witness_never_reaches_a_live_session_operation()
    {
        using var rig = new Rig(TenantContextOrigin.HostAndOwnDataRequestScope, Branch, withWitness: true);
        rig.Witnesses.Purpose = OwnDataRequestPurpose.Create;
        rig.Witnesses.Group = new DataRequestScopeGroup(Tenant, Branch);

        var result = await rig.RunAsync(new CreateOwnDataExportCommand());

        result.Error.Should().BeSameAs(TenantContextFactory.Refused,
            "an OwnDataRequest operation needs an ordinary authenticated context, which the retained origin is not");
        rig.Witnesses.Captured.Should().Be(0);
    }

    // ---- The origin's ceiling ------------------------------------------------------------------------------

    /// <summary>
    /// The retained-entry path reaches nothing outside its own set: its origin admits only an
    /// <see cref="OperationPolicyKind.OwnDataRequestEntry"/> request, and the consent origin never admits one.
    /// </summary>
    [Theory]
    [InlineData("retained-origin-entry-kind", true)]
    [InlineData("retained-origin-live-kind", false)]
    [InlineData("retained-origin-consent-kind", false)]
    [InlineData("retained-origin-unregistered", false)]
    [InlineData("consent-origin-entry-kind", false)]
    [InlineData("live-origin-entry-kind", true)]
    public async Task The_retained_origin_reaches_only_its_own_policy_kind(string label, bool reaches)
    {
        var catalog = new OperationPolicyCatalog([new ProbePolicies()]);
        ITenantContext context = label switch
        {
            "consent-origin-entry-kind" => TenantContextFactory.CreateForConsentSubjectScope(Tenant, null, Actor, null, null).Value!,
            "live-origin-entry-kind" => TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HostTenantId = Tenant,
                ClaimTenantId = Tenant,
                HasValidatedPrincipal = true,
                UserId = Actor,
                MembershipCovers = true,
            }).Value!,
            _ => TenantContextFactory.CreateForOwnDataRequestScope(Tenant, null, Actor, new DataRequestScopeGroup(Tenant, null), null).Value!,
        };
        var called = label switch
        {
            "retained-origin-live-kind" => await ReachesAsync<CreateProbe>(context, catalog),
            "retained-origin-consent-kind" => await ReachesAsync<ConsentProbe>(context, catalog),
            "retained-origin-unregistered" => await ReachesAsync<UnregisteredProbe>(context, catalog),
            _ => await ReachesAsync<ReadProbe>(context, catalog),
        };
        // An ordinary authenticated origin reaches an unmarked request at this gate; authorization at step 5 then
        // refuses the entry kind outside its origin (Entry_without_exactly_one_live_witness_in_its_own_origin_is_not_found).
        called.Should().Be(reaches);
    }

    private static async Task<bool> ReachesAsync<TRequest>(ITenantContext context, IOperationPolicyCatalog catalog)
        where TRequest : MediatR.IRequest<Result<None>>, new()
    {
        var behavior = new LearnStack.Application.Pipeline.TenantContextBehavior<TRequest, Result<None>>(context, catalog);
        var called = false;
        await behavior.Handle(new TRequest(), () => { called = true; return Task.FromResult(Result.Ok(None.Value)); }, CancellationToken.None);
        return called;
    }

    // ---- Witness, factory and decision shapes -------------------------------------------------------------

    [Theory]
    [InlineData("list", "ReadOnly")]
    [InlineData("read", "ReadOnly")]
    [InlineData("create", "ReadWrite")]
    [InlineData("cancel", "ReadWrite")]
    [InlineData("ticket", "ReadWrite")]
    [InlineData("chunk", "ReadWrite")]
    public void Each_purpose_has_its_fixed_transaction_mode_and_shape(string label, string mode)
    {
        var purpose = Enum.Parse<OwnDataRequestPurpose>(label, ignoreCase: true);
        var group = purpose == OwnDataRequestPurpose.List ? null : new DataRequestScopeGroup(Tenant, Branch);
        var witness = WitnessFor(purpose, group, purpose == OwnDataRequestPurpose.List ? Page() : null);
        witness.Mode.Should().Be(Enum.Parse<TransactionMode>(mode));
        witness.TenantId.Should().Be(Tenant);
        witness.OrganizationId.Should().Be(purpose == OwnDataRequestPurpose.List ? null : Branch);
        witness.ToString().Should().Be(nameof(OwnDataRequestScopeWitness), "no subject or scope reaches a log through the witness");
    }

    [Theory]
    [InlineData("list-without-page")]
    [InlineData("list-with-group")]
    [InlineData("read-without-group")]
    [InlineData("read-with-page")]
    [InlineData("foreign-tenant-group")]
    [InlineData("other-branch-on-branch-host")]
    [InlineData("tenant-wide-group-on-branch-host")]
    [InlineData("page-outside-host")]
    [InlineData("page-duplicate")]
    [InlineData("page-too-long")]
    [InlineData("system-actor")]
    [InlineData("sentinel-host")]
    [InlineData("blank-host")]
    public void The_witness_refuses_every_shape_outside_its_host_and_purpose(string label)
    {
        var branchHost = label is "other-branch-on-branch-host" or "tenant-wide-group-on-branch-host" or "page-outside-host";
        var foreign = TenantId.From(Guid.CreateVersion7());
        var (purpose, group, page) = label switch
        {
            "list-without-page" => (OwnDataRequestPurpose.List, (DataRequestScopeGroup?)null, (OwnDataRequestScopePage?)null),
            "list-with-group" => (OwnDataRequestPurpose.List, new DataRequestScopeGroup(Tenant, null), Page()),
            "read-without-group" => (OwnDataRequestPurpose.Read, null, null),
            "read-with-page" => (OwnDataRequestPurpose.Read, new DataRequestScopeGroup(Tenant, null), Page()),
            "foreign-tenant-group" => (OwnDataRequestPurpose.Read, new DataRequestScopeGroup(foreign, null), null),
            "other-branch-on-branch-host" => (OwnDataRequestPurpose.Read, new DataRequestScopeGroup(Tenant, OtherBranch), null),
            "tenant-wide-group-on-branch-host" => (OwnDataRequestPurpose.Read, new DataRequestScopeGroup(Tenant, null), null),
            "page-outside-host" => (OwnDataRequestPurpose.List, null, Page(new DataRequestScopeGroup(Tenant, OtherBranch))),
            "page-duplicate" => (OwnDataRequestPurpose.List, null, Page(new DataRequestScopeGroup(Tenant, null), new DataRequestScopeGroup(Tenant, null))),
            "page-too-long" => (OwnDataRequestPurpose.List, null, new OwnDataRequestScopePage([.. Enumerable.Range(0, 21)
                .Select(_ => new DataRequestScopeGroup(Tenant, OrganizationId.From(Guid.CreateVersion7())))], null)),
            _ => (OwnDataRequestPurpose.Read, new DataRequestScopeGroup(Tenant, null), null),
        };
        var actor = label == "system-actor" ? UserId.SystemActor : Actor;
        var host = label == "sentinel-host" ? TenantId.PlatformSentinel : Tenant;
        var hostName = label == "blank-host" ? " " : "school.invalid";
        var build = () => WitnessFor(purpose, group, page, branchHost ? Branch : null, actor, host, hostName);
        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("tenant-host-tenant-wide")]
    [InlineData("tenant-host-organization")]
    [InlineData("branch-host-branch")]
    [InlineData("tenant-host-list")]
    public void The_factory_admits_the_retained_group_within_its_host(string label)
    {
        var (hostOrganization, selected) = label switch
        {
            "tenant-host-tenant-wide" => ((OrganizationId?)null, new DataRequestScopeGroup(Tenant, null)),
            "tenant-host-organization" => (null, new DataRequestScopeGroup(Tenant, Branch)),
            "branch-host-branch" => (Branch, new DataRequestScopeGroup(Tenant, Branch)),
            _ => ((OrganizationId?)null, (DataRequestScopeGroup?)null),
        };
        var context = TenantContextFactory.CreateForOwnDataRequestScope(Tenant, hostOrganization, Actor, selected, "trace").Value!;
        context.Origin.Should().Be(TenantContextOrigin.HostAndOwnDataRequestScope);
        context.TenantId.Should().Be(Tenant);
        context.OrganizationId.Should().Be(selected is null ? hostOrganization : selected.OrganizationId);
        context.UserId.Should().Be(Actor);
        context.HostScope!.TenantId.Should().Be(Tenant);
        context.HostScope.OrganizationId.Should().Be(hostOrganization);
    }

    [Theory]
    [InlineData("foreign-tenant")]
    [InlineData("other-branch")]
    [InlineData("tenant-wide-on-branch-host")]
    [InlineData("system-actor")]
    [InlineData("sentinel-host")]
    public void The_factory_refuses_a_group_outside_its_host_and_a_non_person(string label)
    {
        var refused = label switch
        {
            "foreign-tenant" => TenantContextFactory.CreateForOwnDataRequestScope(Tenant, null, Actor,
                new DataRequestScopeGroup(TenantId.From(Guid.CreateVersion7()), null), null),
            "other-branch" => TenantContextFactory.CreateForOwnDataRequestScope(Tenant, Branch, Actor,
                new DataRequestScopeGroup(Tenant, OtherBranch), null),
            "tenant-wide-on-branch-host" => TenantContextFactory.CreateForOwnDataRequestScope(Tenant, Branch, Actor,
                new DataRequestScopeGroup(Tenant, null), null),
            "system-actor" => TenantContextFactory.CreateForOwnDataRequestScope(Tenant, null, UserId.SystemActor, null, null),
            _ => TenantContextFactory.CreateForOwnDataRequestScope(TenantId.PlatformSentinel, null, Actor, null, null),
        };
        refused.IsFailure.Should().BeTrue();
        refused.Error.Should().BeSameAs(TenantContextFactory.Refused);
    }

    [Fact]
    public void Policies_of_both_kinds_carry_no_permission_and_no_alternatives()
    {
        foreach (var policy in new[] { OperationPolicy.ForOwnDataRequest<CreateProbe>("identity.data_request.create"),
            OperationPolicy.ForOwnDataRequestEntry<ReadProbe>("identity.data_request.read") })
        {
            policy.RequiredPermission.Should().BeNull();
            policy.Alternatives.Should().BeEmpty();
            policy.AllowedSources.Should().BeEmpty();
            policy.ReadScope.Should().Be(OperationReadScope.Scoped);
            policy.RequiresOrglessWrite.Should().BeFalse();
        }
    }

    // ---- Doubles ------------------------------------------------------------------------------------------

    private static OwnDataRequestScopePage Page(params DataRequestScopeGroup[] groups) =>
        new(groups.Length == 0 ? [new DataRequestScopeGroup(Tenant, null)] : [.. groups], null);

    private static OwnDataRequestScopeWitness WitnessFor(OwnDataRequestPurpose purpose, DataRequestScopeGroup? group,
        OwnDataRequestScopePage? page, OrganizationId? hostOrganization = null, UserId? actor = null, TenantId? host = null,
        string hostName = "school.invalid")
    {
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(true);
        context.TenantId.Returns(Tenant);
        context.UserId.Returns(Actor);
        context.Origin.Returns(TenantContextOrigin.HostAndOwnDataRequestScope);
        var invocations = new InvocationSource();
        var invocation = invocations.Open(new ReadProbe(), OperationPolicy.ForOwnDataRequestEntry<ReadProbe>("identity.data_request.read"), context);
        using var source = new WitnessSource
        {
            Purpose = purpose,
            Group = group,
            Page = page,
            HostOrganization = hostOrganization,
            Actor = actor ?? Actor,
            HostTenant = host ?? Tenant,
            HostName = hostName
        };
        return source.ForInvocation(invocation)!;
    }

    private sealed record CreateProbe : MediatR.IRequest<Result<None>>;
    private sealed record ReadProbe : MediatR.IRequest<Result<None>>;
    private sealed record ForeignOwnProbe : MediatR.IRequest<Result<None>>;
    private sealed record ConsentProbe : MediatR.IRequest<Result<None>>;
    private sealed record UnregisteredProbe : MediatR.IRequest<Result<None>>;

    private sealed class ProbePolicies : IOperationPolicySource
    {
        public IEnumerable<PermissionRegistration> Definitions => [];
        public IEnumerable<OperationPolicy> Policies =>
        [
            OperationPolicy.ForOwnDataRequest<CreateProbe>("identity.data_request.create"),
            OperationPolicy.ForOwnDataRequest<CreateOwnDataExportCommand>(IdentityOperationPolicySource.OwnDataExportCreateOperation),
            OperationPolicy.ForOwnDataRequest<ListOwnDataRequestsQuery>("identity.data_request.list_probe"),
            OperationPolicy.ForOwnDataRequestEntry<ReadProbe>("identity.data_request.read"),
            OperationPolicy.ForOwnDataRequest<ForeignOwnProbe>("identity.membership.read_own_probe"),
            OperationPolicy.ForOwnConsentEntry<ConsentProbe>("tenancy.membership_field_consent.probe"),
        ];
    }

    private sealed class Rig : IDisposable
    {
        private readonly OperationAuthorizationCoordinator _coordinator;
        private readonly CandidateSource _candidates;
        private readonly WitnessSource _second = new();

        public Rig(TenantContextOrigin origin, OrganizationId? organization, bool withWitness = false, bool twoSources = false,
            AuthorizationSourceKind candidateKind = AuthorizationSourceKind.Http)
        {
            Context = origin == TenantContextOrigin.HostAndOwnDataRequestScope
                ? TenantContextFactory.CreateForOwnDataRequestScope(Tenant, null, Actor,
                    new DataRequestScopeGroup(Tenant, organization), null).Value!
                : LiveContext(origin, organization);
            _candidates = new CandidateSource(Context, candidateKind);
            var catalog = new OperationPolicyCatalog([new ProbePolicies()]);
            _second.Group = new DataRequestScopeGroup(Tenant, organization);
            IOwnDataRequestScopeSource[] sources = !withWitness ? [] : twoSources ? [Witnesses, _second] : [Witnesses];
            var authorizer = new IdentityOperationAuthorizer(() => Reader, catalog, dataRequestSources: sources);
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(DateTimeOffset.UtcNow);
            OperationAuthorizationCoordinator? current = null;
            var evidence = new AuthorizationEvidenceCoordinator(Substitute.For<IAuditStore>(), new AuditStateCapture(),
                Substitute.For<IUnitOfWork>(), clock, new SystemGuidFactory(), () => current!, [],
                NullLogger<AuthorizationEvidenceCoordinator>.Instance);
            _coordinator = current = new OperationAuthorizationCoordinator(catalog, authorizer, Context, [_candidates], [], evidence);
        }

        public ITenantContext Context { get; }
        public IIdentityAuthorizationReader Reader { get; } = Substitute.For<IIdentityAuthorizationReader>();
        public WitnessSource Witnesses { get; } = new();
        public ProtectedOperationDecision? Decision { get; private set; }

        public Task<Result<None>> RunAsync<TRequest>(TRequest request) where TRequest : notnull =>
            _coordinator.ExecuteRequestAsync(request, () =>
            {
                Decision = _coordinator.CurrentDecision;
                return Task.FromResult(Result.Ok(None.Value));
            });

        public void Dispose()
        {
            _coordinator.Dispose();
            _candidates.Dispose();
            Witnesses.Dispose();
            _second.Dispose();
        }

        private static ITenantContext LiveContext(TenantContextOrigin origin, OrganizationId? organization)
        {
            var context = Substitute.For<ITenantContext>();
            context.IsResolved.Returns(true);
            context.TenantId.Returns(Tenant);
            context.OrganizationId.Returns(organization);
            context.UserId.Returns(Actor);
            context.Origin.Returns(origin);
            return context;
        }
    }

    private sealed class CandidateSource(ITenantContext context, AuthorizationSourceKind kind)
        : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        public AuthorizationCandidate? ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            tenant == context.TenantId && organization == context.OrganizationId
                ? ForCandidate(kind, context.UserId!.Value, tenant, organization) : null;
    }

    private sealed class InvocationSource : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        public OperationInvocation Open(object operation, OperationPolicy policy, ITenantContext context) =>
            _current = ForInvocation(operation, policy, context, null, null, CancellationToken.None);
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }

    /// <summary>Test-only captured provenance; it claims no HTTP credential admission.</summary>
    private sealed class WitnessSource : OwnDataRequestScopeWitness.MintingSource, IOwnDataRequestScopeSource
    {
        private readonly object _evidence = new();
        private OperationInvocation? _current;
        public OwnDataRequestPurpose Purpose { get; set; } = OwnDataRequestPurpose.Read;
        public DataRequestScopeGroup? Group { get; set; }
        public OwnDataRequestScopePage? Page { get; set; }
        public OrganizationId? HostOrganization { get; set; }
        public UserId Actor { get; set; } = OwnDataRequestPolicyTests.Actor;
        public TenantId HostTenant { get; set; } = Tenant;
        public string HostName { get; set; } = "school.invalid";
        public int Captured { get; private set; }

        public OwnDataRequestScopeWitness? ForInvocation(OperationInvocation invocation)
        {
            if (!IsOpen || invocation.Policy.Kind != OperationPolicyKind.OwnDataRequestEntry) return null;
            _current = invocation;
            Captured++;
            return Capture(_evidence, invocation, Subject, Actor, HostTenant, HostOrganization, HostName, Purpose, Group, Page);
        }

        protected override bool IsEvidenceCurrent(object evidence, OperationInvocation invocation) =>
            ReferenceEquals(evidence, _evidence) && ReferenceEquals(invocation, _current);
    }
}

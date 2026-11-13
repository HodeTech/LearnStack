using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using MediatR;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// The real federation handlers over a stub store (C-2 of the Step 2 review). Each request the runner sends runs the
/// production <see cref="FederationCommandHandlers"/> or <see cref="GetFederationConnectionQueryHandler"/> with the real
/// <see cref="FederationAuthorization"/>, a real <see cref="FederationConnection"/> and the catalogue's own policy, inside
/// a simulated transaction. Nothing here re-implements a handler: the authorizer stub only answers "is the current
/// actor still an administrator", and the store only holds the one aggregate and counts its saves.
/// </summary>
internal sealed class FederationHarness : IDisposable
{
    internal static readonly UserId AdminA = UserId.From(Guid.CreateVersion7());
    internal static readonly UserId AdminB = UserId.From(Guid.CreateVersion7());
    internal static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());

    private readonly OperationPolicyCatalog _catalog = new([new IdentityOperationPolicySource(), new TenancyOperationPolicySource(),
        new CustomizationOperationPolicySource()]);
    private readonly CandidateSource _candidates = new();
    private readonly InvocationSource _invocations = new();
    private readonly TimeSpan _providerBudget;

    internal FederationHarness(TimeSpan? provider = null)
    {
        _providerBudget = provider ?? TimeSpan.FromSeconds(5);
        Admins = [AdminA, AdminB];
        Features.IsEnabledAsync(Arg.Any<FeatureKey>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Entitled));
        Authorizer.AuthorizeAsync(Arg.Any<OperationInvocation>(), Arg.Any<OperationPolicy>(), Arg.Any<AuthorizationCandidate>(),
            Arg.Any<CancellationToken>()).Returns(call => DecisionSource.Decide(call.ArgAt<OperationInvocation>(0), call.ArgAt<AuthorizationCandidate>(2),
                Admins.Contains(Actor) ? ProtectedOperationOutcome.Allowed : ProtectedOperationOutcome.Forbidden));
        Sender.Send(Arg.Any<IRequest<Result<FederationPendingOperation>>>(), Arg.Any<CancellationToken>())
            .Returns(call => DispatchAsync<FederationPendingOperation>(call.ArgAt<object>(0), call.ArgAt<CancellationToken>(1)));
        Sender.Send(Arg.Any<IRequest<Result<FederationOperationReceipt>>>(), Arg.Any<CancellationToken>())
            .Returns(call => DispatchAsync<FederationOperationReceipt>(call.ArgAt<object>(0), call.ArgAt<CancellationToken>(1)));
        Sender.Send(Arg.Any<IRequest<Result<FederationConnectionRead>>>(), Arg.Any<CancellationToken>())
            .Returns(call => DispatchAsync<FederationConnectionRead>(call.ArgAt<object>(0), call.ArgAt<CancellationToken>(1)));
        Store = new StubStore(this);
        RunnerContext = new FixedTenant();
    }

    internal InMemoryFederationProvider Provider { get; } = new();
    internal InMemoryFederationEgressFetcher Egress { get; } = new();
    internal IFeatureFlags Features { get; } = Substitute.For<IFeatureFlags>();
    internal ISender Sender { get; } = Substitute.For<ISender>();
    internal MovableClock Clock { get; } = new();
    internal IProtectedOperationAuthorizer Authorizer { get; } = Substitute.For<IProtectedOperationAuthorizer>();
    internal IOperationInvocationContext Invocations { get; } = Substitute.For<IOperationInvocationContext>();
    internal IIdentityMutationLocks Locks { get; } = Substitute.For<IIdentityMutationLocks>();
    internal IAuditSubject Subject { get; } = Substitute.For<IAuditSubject>();
    internal StubStore Store { get; }
    internal ITenantContext RunnerContext { get; }
    internal HashSet<UserId> Admins { get; }
    internal UserId Actor { get; set; } = AdminA;
    internal bool Entitled { get; set; } = true;
    internal bool TransactionOpen { get; private set; }
    internal FederationConnection? Connection { get; set; }
    internal List<string> Log { get; } = [];
    internal int SavesInLastRequest { get; set; }
    internal FederationBrokerWrite? LastWrite { get; set; }

    /// <summary>Calls the provider makes, through a wrapper that proves no transaction is open across them.</summary>
    internal IFederationProvider ObservedProvider => new Observing(this);

    internal FederationOperationRunner Runner() => new(Sender, ObservedProvider, Egress, Features, RunnerContext, new Ids(), Clock,
        FederationOperationBudgets.Default with { Provider = _providerBudget, Complete = TimeSpan.FromSeconds(5), DatabaseStep = TimeSpan.FromSeconds(5) });

    /// <summary>Sends one request straight to its real handler, as one top-level frame.</summary>
    internal Task<Result<T>> SendAsync<T>(IRequest<Result<T>> request) => DispatchAsync<T>(request, CancellationToken.None);

    public void Dispose() => _candidates.Dispose();

    private async Task<Result<T>> DispatchAsync<T>(object request, CancellationToken cancellationToken)
    {
        TransactionOpen = true;
        SavesInLastRequest = 0;
        try
        {
            Log.Add(request is CompleteFederationOperationCommand completing ? "complete:" + completing.Outcome : "begin:" + request.GetType().Name);
            var context = TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HasValidatedPrincipal = true,
                HostTenantId = Tenant,
                ClaimTenantId = Tenant,
                UserId = Actor,
                MembershipCovers = true,
                ClaimedOrganizationBelongsToTenant = true,
            }).Value!;
            Assert.True(_catalog.TryGet(request.GetType(), out var policy), request.GetType().Name + " is catalogued");
            var candidate = _candidates.Mint(context.OrganizationId, Actor);
            var invocation = _invocations.Mint(request, policy!, context, candidate);
            Invocations.CurrentInvocation.Returns(invocation);
            Invocations.CurrentDecision.Returns(DecisionSource.Decide(invocation, candidate, ProtectedOperationOutcome.Allowed));
            var authorization = new FederationAuthorization(Invocations, _catalog, Authorizer, context, Substitute.For<IAuthorizationRefusalReporter>());
            var handlers = new FederationCommandHandlers(Store, authorization, context, Locks, Features, Subject, new Ids(), Clock);
            object result = request switch
            {
                BeginConfigureFederationCommand value => await handlers.Handle(value, cancellationToken),
                BeginRotateFederationSecretCommand value => await handlers.Handle(value, cancellationToken),
                BeginRenameFederationCommand value => await handlers.Handle(value, cancellationToken),
                BeginFederationTestStartCommand value => await handlers.Handle(value, cancellationToken),
                BeginDisableFederationCommand value => await handlers.Handle(value, cancellationToken),
                BeginDeleteFederationCommand value => await handlers.Handle(value, cancellationToken),
                ResumeFederationOperationCommand value => await handlers.Handle(value, cancellationToken),
                CompleteFederationOperationCommand value => await handlers.Handle(value, cancellationToken),
                EnableFederationCommand value => await handlers.Handle(value, cancellationToken),
                ActivateFederationCommand value => await handlers.Handle(value, cancellationToken),
                RecordFederationTestResultCommand value => await handlers.Handle(value, cancellationToken),
                ReconcileFederationConnectionCommand value => await handlers.Handle(value, cancellationToken),
                GetFederationConnectionQuery value => await new GetFederationConnectionQueryHandler(Store, authorization).Handle(value, cancellationToken),
                _ => throw new InvalidOperationException(request.GetType().Name),
            };
            return (Result<T>)result;
        }
        finally { TransactionOpen = false; }
    }

    internal sealed class MovableClock : IClock
    {
        private DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class Ids : IGuidFactory
    {
        public Guid NewUuidV7() => Guid.CreateVersion7();
        public Guid NewUuidV4() => Guid.NewGuid();
    }

    /// <summary>The one aggregate of the current tenant, through the live filter; each save is counted.</summary>
    internal sealed class StubStore(FederationHarness harness) : IFederationConnectionStore
    {
        internal int Saves { get; private set; }

        public Task<FederationConnection?> FindLiveLockedAsync(CancellationToken cancellationToken) => Task.FromResult(Live());
        public Task<FederationConnection?> ReadLiveAsync(CancellationToken cancellationToken) => Task.FromResult(Live());

        public Task AddAsync(FederationConnection aggregate, CancellationToken cancellationToken = default)
        {
            Assert.True(harness.TransactionOpen, "a save happens only inside a request");
            harness.Connection = aggregate;
            Saved();
            return Task.CompletedTask;
        }

        public Task UpdateAsync(FederationConnection aggregate, CancellationToken cancellationToken = default)
        {
            Assert.True(harness.TransactionOpen, "a save happens only inside a request");
            Assert.Same(harness.Connection, aggregate);
            Saved();
            return Task.CompletedTask;
        }

        private FederationConnection? Live() => harness.Connection is { } connection
            ? new[] { connection }.AsQueryable().Live().SingleOrDefault() : null;

        private void Saved()
        {
            Saves++;
            harness.SavesInLastRequest++;
        }
    }

    private sealed class FixedTenant : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => Tenant;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => null;
        public string? CorrelationId => "federation-unit";
        public string? ModuleName => "identity";
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate Mint(OrganizationId? organization, UserId actor) =>
            ForCandidate(AuthorizationSourceKind.Http, actor, Tenant, organization);
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
        public static ProtectedOperationDecision Decide(OperationInvocation invocation, AuthorizationCandidate candidate,
            ProtectedOperationOutcome outcome) => outcome == ProtectedOperationOutcome.Allowed
            ? ForDecision(invocation, candidate, outcome, [Guid.CreateVersion7()], [Guid.CreateVersion7()])
            : ForDecision(invocation, candidate, outcome);
    }

    /// <summary>Records the provider calls in the log and proves no transaction is open across them.</summary>
    private sealed class Observing(FederationHarness harness) : IFederationProvider
    {
        public Task<FederationProviderResult> ApplyAsync(FederationBrokerWrite write, CancellationToken cancellationToken = default)
        { Observe("apply"); harness.LastWrite = write; return harness.Provider.ApplyAsync(write, cancellationToken); }
        public Task<FederationBrokerReadback> ReadAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
        { Observe("read"); return harness.Provider.ReadAsync(owner, cancellationToken); }
        public Task<FederationProviderResult> DeleteAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
        { Observe("delete"); return harness.Provider.DeleteAsync(owner, cancellationToken); }
        private void Observe(string call)
        {
            Assert.False(harness.TransactionOpen, "no database transaction is open across provider I/O");
            harness.Log.Add("provider:" + call);
        }
    }
}

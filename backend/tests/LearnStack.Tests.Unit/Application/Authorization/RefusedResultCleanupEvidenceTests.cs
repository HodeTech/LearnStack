using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Authorization;

/// <summary>A decided handler refusal precedes fallible transaction cleanup in the actual common owner graph.</summary>
public sealed class RefusedResultCleanupEvidenceTests
{
    [Fact]
    public async Task A_completed_handler_refusal_survives_its_token_cancellation_before_transaction_observation()
    {
        using var proof = new Proof("clean");
        var result = await proof.RunAsync(Error("forbidden"), cancelBeforeTransactionObservation: true);
        result.Error!.Code.Should().Be("forbidden");
        proof.Cancellation.IsCancellationRequested.Should().BeTrue();
        proof.ClockReadsAtCleanup.Should().Be(1);
        proof.Standalone.Should().ContainSingle().Which.Draft.Outcome.Should().Be(AuditOutcome.Denied);
    }

    [Theory]
    [InlineData("ownerless")]
    [InlineData("read-only")]
    public async Task Ownerless_or_readonly_refusal_keeps_its_observation_after_the_handler_cancels_its_own_token(string operation)
    {
        using var proof = new Proof("clean");
        var result = await proof.RunAsync(Error("not_found"), transactionOperation: operation,
            cancelBeforeTransactionObservation: true);
        result.Error!.Code.Should().Be("not_found");
        proof.Authorization.CurrentInvocation.Should().BeNull("audit observation never revives canceled execution authority");
        proof.Authorization.CurrentDecision.Should().BeNull();
        proof.Standalone.Should().ContainSingle().Which.Draft.ErrorKey.Should().Be("lockey_not_found");
        proof.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_uses_the_latest_verified_postlock_context_instead_of_the_initial_observation()
    {
        using var proof = new Proof("clean");
        var postlockMembership = Guid.CreateVersion7();
        await proof.RunAsync(Error("forbidden"), cancelBeforeTransactionObservation: true,
            beforeResult: () => proof.Evidence.ReportRecheck(DecisionSource.Recheck(proof.Authorization.CurrentInvocation!, postlockMembership)));
        var metadata = proof.Standalone.Should().ContainSingle().Which.Draft.Metadata!;
        metadata.Should().Contain(postlockMembership.ToString("D")).And.NotContain(proof.Authorizer.Membership.ToString("D"));
    }

    [Fact]
    public async Task Cancellation_before_policy_admission_cannot_create_a_historical_observation()
    {
        using var proof = new Proof("clean");
        proof.Cancellation.Cancel();
        await FluentActions.Awaiting(() => proof.RunAsync(Error("forbidden"))).Should().ThrowAsync<OperationCanceledException>();
        proof.HandlerCalls.Should().Be(0);
        proof.Authorizer.Calls.Should().Be(0);
        proof.Standalone.Should().BeEmpty();
        proof.Clock.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Disposing_the_operation_source_closes_its_observation_even_while_the_handler_returns_a_refusal()
    {
        using var proof = new Proof("clean");
        await proof.RunAsync(Error("forbidden"), beforeResult: proof.Authorization.Dispose);
        proof.Authorization.CurrentInvocation.Should().BeNull();
        proof.Standalone.Should().BeEmpty();
        proof.Clock.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData("closed-scope")]
    [InlineData("foreign-scope")]
    [InlineData("foreign-request")]
    [InlineData("consumed-result")]
    [InlineData("other-invocation")]
    public async Task Opaque_observation_cannot_cross_scope_request_invocation_or_result_lifetimes(string changed)
    {
        using var proof = new Proof("clean");
        await proof.RunAsync(Error("validation_failed"), beforeResult: () =>
        {
            var request = proof.Authorization.CurrentInvocation!.Operation;
            var capture = typeof(AuthorizationEvidenceCoordinator).GetMethod("CaptureRefusalObservation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var record = typeof(AuthorizationEvidenceCoordinator).GetMethod("RecordRefusalResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var scope = proof.Evidence.BeginExecution();
            try
            {
                var observation = capture.Invoke(proof.Evidence, [scope, request.GetType(), request]);
                observation.Should().NotBeNull("the probe borrows a real active admitted observation, never a constructed token");
                if (changed == "closed-scope") scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (changed == "foreign-scope")
                {
                    using var foreign = new Proof("clean");
                    var foreignScope = foreign.Evidence.BeginExecution();
                    try
                    {
                        capture.Invoke(proof.Evidence, [foreignScope, request.GetType(), request]).Should().BeNull();
                    }
                    finally { foreignScope.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    return;
                }
                if (changed == "consumed-result")
                    record.Invoke(proof.Evidence, [observation, request.GetType(), request, Result.Ok(None.Value)]);
                if (changed == "other-invocation")
                {
                    proof.Authorization.ExecuteRequestAsync<Command, Result<None>>((Command)request, () =>
                    {
                        record.Invoke(proof.Evidence, [observation, request.GetType(), request, Result.Fail<None>(Error("forbidden"))]);
                        return Task.FromResult(Result.Ok(None.Value));
                    }).GetAwaiter().GetResult();
                    return;
                }
                record.Invoke(proof.Evidence, [observation, request.GetType(), changed == "foreign-request" ? new Command() : request,
                    Result.Fail<None>(Error("forbidden"))]);
            }
            finally { scope.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        proof.Standalone.Should().BeEmpty();
        proof.Clock.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData("throw", "forbidden")]
    [InlineData("throw", "not_found")]
    [InlineData("throw", "resource_scope_violation")]
    [InlineData("throw", "feature_disabled")]
    [InlineData("throw", "limit_exceeded")]
    [InlineData("cancel", "forbidden")]
    [InlineData("cancel", "not_found")]
    [InlineData("cancel", "resource_scope_violation")]
    [InlineData("cancel", "feature_disabled")]
    [InlineData("cancel", "limit_exceeded")]
    public async Task Decided_policy_refusal_is_frozen_before_cleanup_can_throw_or_cancel_its_invocation(string cleanup, string code)
    {
        using var proof = new Proof(cleanup);
        if (cleanup == "throw")
            (await Record.ExceptionAsync(() => proof.RunAsync(Error(code)))).Should().BeSameAs(proof.RollbackFailure);
        else
        {
            var result = await proof.RunAsync(Error(code));
            result.IsFailure.Should().BeTrue();
            result.Error!.Code.Should().Be(code);
            proof.Cancellation.IsCancellationRequested.Should().BeTrue();
        }

        proof.HandlerCalls.Should().Be(1, "the policy admitted this act before its handler made the refusal");
        proof.Authorizer.Calls.Should().Be(1);
        proof.ClockReadsAtCleanup.Should().Be(1, "the decided event must exist before the first FailAsync starts");
        var occurrence = proof.Standalone.Should().ContainSingle().Subject;
        occurrence.Kind.Should().Be(AuthorizationEvidenceKind.Denial);
        occurrence.Draft.Operation.Should().Be("identity.authorization.deny");
        occurrence.Draft.Outcome.Should().Be(AuditOutcome.Denied);
        occurrence.Draft.ErrorKey.Should().Be("lockey_" + code);
        occurrence.Draft.Timestamp.Should().Be(Proof.DecisionTime);
        occurrence.Draft.Metadata.Should().Contain("fixture.cleanup.command");
        occurrence.Draft.TenantId.Should().Be(Proof.Tenant);
        occurrence.Draft.ActorUserId.Should().Be(Proof.Actor);
        occurrence.Draft.Id.Value.Version.Should().Be(7);
        proof.StandaloneTokens.Should().ContainSingle().Which.Should().Be(CancellationToken.None);
        proof.WroteWhileTransactionActive.Should().BeFalse();
        proof.Pending.Should().BeEmpty("a refused owner rolls back instead of staging success evidence");
    }

    [Fact]
    public async Task Normal_cleanup_and_the_outer_policy_observer_share_one_fixed_refusal_occurrence()
    {
        using var proof = new Proof("clean");
        var result = await proof.RunAsync(Error("forbidden"));

        result.Error!.Code.Should().Be("forbidden");
        proof.FailCalls.Should().Be(1);
        proof.Clock.Reads.Should().Be(1, "transaction observation and the outer policy observation must deduplicate by invocation");
        proof.Standalone.Should().ContainSingle().Which.Draft.Timestamp.Should().Be(Proof.DecisionTime);
    }

    [Theory]
    [InlineData("throw", "dependency_unavailable")]
    [InlineData("throw", "audit_unavailable")]
    [InlineData("throw", "validation_failed")]
    [InlineData("throw", "concurrency_conflict")]
    [InlineData("cancel", "dependency_unavailable")]
    [InlineData("cancel", "audit_unavailable")]
    [InlineData("cancel", "validation_failed")]
    [InlineData("cancel", "concurrency_conflict")]
    public async Task Cleanup_faults_do_not_reclassify_503_validation_or_conflict_results_as_authorization_denials(string cleanup, string code)
    {
        using var proof = new Proof(cleanup);
        if (cleanup == "throw")
            (await Record.ExceptionAsync(() => proof.RunAsync(Error(code)))).Should().BeSameAs(proof.RollbackFailure);
        else (await proof.RunAsync(Error(code))).Error!.Code.Should().Be(code);

        proof.Clock.Reads.Should().Be(0);
        proof.Standalone.Should().BeEmpty();
        proof.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task An_arbitrary_handler_exception_with_refusal_words_is_not_an_already_decided_policy_result()
    {
        using var proof = new Proof("clean");
        var primary = new IOException("forbidden not_found resource_scope_violation are exception text, not a result");
        (await Record.ExceptionAsync(() => proof.RunAsync(Error("forbidden"), primary))).Should().BeSameAs(primary);
        proof.Standalone.Should().BeEmpty();
        proof.Clock.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("different-request")]
    public async Task An_unrelated_transaction_operation_cannot_mint_a_refusal_for_the_current_policy_invocation(string operation)
    {
        using var proof = new Proof("throw");
        (await Record.ExceptionAsync(() => proof.RunAsync(Error("forbidden"), transactionOperation: operation)))
            .Should().BeSameAs(proof.RollbackFailure);
        proof.HandlerCalls.Should().Be(1);
        proof.ClockReadsAtCleanup.Should().Be(0);
        proof.Standalone.Should().BeEmpty();
    }

    private static Error Error(string code) => new(new LocalizedMessage("lockey_" + code));
    private sealed record Command : IRequest<Result<None>>;
    private sealed record DifferentCommand : IRequest<Result<None>>;

    private sealed class Proof : IDisposable
    {
        internal static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000fa01"));
        internal static readonly UserId Actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000fa02"));
        internal static readonly DateTimeOffset DecisionTime = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        private readonly CandidateSource _candidates = new();
        private readonly OperationAuthorizationCoordinator _authorization;
        private readonly AuthorizationEvidenceCoordinator _evidence;
        private readonly DomainEventCollector _collector;
        private readonly AuditLogBehavior<Command, Result<None>> _audit;
        private readonly AuthorizationBehavior<Command, Result<None>> _policy;
        private readonly TransactionExecutionCoordinator _transactions;
        private readonly IUnitOfWork _unit;
        private bool _active;
        private bool _rollbackOnly;
        private TransactionMode _mode;

        internal Proof(string cleanup)
        {
            _unit = Substitute.For<IUnitOfWork>();
            var transaction = Substitute.For<DbTransaction>();
            var frame = Substitute.For<IUnitOfWorkScope>();
            frame.IsOwner.Returns(true);
            _unit.HasActiveTransaction.Returns(_ => _active);
            _unit.Transaction.Returns(_ => _active ? transaction : null);
            _unit.Mode.Returns(_ => _active ? _mode : null);
            _unit.IsRollbackOnly.Returns(_ => _rollbackOnly);
            _unit.When(unit => unit.MarkRollbackOnly()).Do(_ => _rollbackOnly = true);
            _unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction>()).Returns(call =>
                _active && ReferenceEquals(call.Arg<DbTransaction>(), transaction) ? new TenantContextAnnouncement(1, Tenant, null) : null);
            _unit.BeginTransactionAsync(Arg.Any<TransactionMode>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                _active = true;
                _mode = call.Arg<TransactionMode>();
                return Task.FromResult(frame);
            });
            frame.FailAsync(Arg.Any<CancellationToken>()).Returns(call =>
            {
                call.Arg<CancellationToken>().Should().Be(CancellationToken.None);
                if (++FailCalls != 1) return Task.CompletedTask;
                ClockReadsAtCleanup = Clock.Reads;
                Clock.Now = DecisionTime.AddDays(1);
                _active = false;
                if (cleanup == "cancel") Cancellation.Cancel();
                return cleanup == "throw" ? Task.FromException(RollbackFailure) : Task.CompletedTask;
            });
            var store = Substitute.For<IAuditStore>();
            store.WriteAuthorizationPendingAsync(_unit, Arg.Any<AuthorizationEvidenceOccurrence>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Pending.Add(call.Arg<AuthorizationEvidenceOccurrence>());
                return Task.CompletedTask;
            });
            store.WriteAuthorizationStandaloneAsync(Arg.Any<AuthorizationEvidenceOccurrence>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var token = call.Arg<CancellationToken>();
                token.ThrowIfCancellationRequested();
                StandaloneTokens.Add(token);
                WroteWhileTransactionActive |= _active;
                Standalone.Add(call.Arg<AuthorizationEvidenceOccurrence>());
                return Task.CompletedTask;
            });
            var context = TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HasValidatedPrincipal = true,
                ClaimTenantId = Tenant,
                UserId = Actor,
                MembershipCovers = true,
                CorrelationId = "fixed-cleanup-denial-proof",
            }).Value!;
            var capture = new AuditStateCapture();
            OperationAuthorizationCoordinator? authorization = null;
            _evidence = new AuthorizationEvidenceCoordinator(store, capture, _unit, Clock, new SystemGuidFactory(),
                () => authorization!, [], NullLogger<AuthorizationEvidenceCoordinator>.Instance);
            _authorization = authorization = new OperationAuthorizationCoordinator(new OperationPolicyCatalog([new PolicySource()]),
                Authorizer, context, [_candidates], [], _evidence);
            var producer = new ProducerExecution(_unit);
            _collector = new DomainEventCollector(producer, Substitute.For<IPublisher>());
            _transactions = new TransactionExecutionCoordinator(_unit, context, store, capture, producer, _collector,
                NullLogger<TransactionExecutionCoordinator>.Instance, _authorization, _evidence);
            _audit = new AuditLogBehavior<Command, Result<None>>(new AuditExecutionCoordinator(new AuditCatalog([new Catalog()]),
                Substitute.For<IAuditConfigService>(), capture, store, context, Clock, new SystemGuidFactory(),
                NullLogger<AuditExecutionCoordinator>.Instance, _unit, _evidence));
            _policy = new AuthorizationBehavior<Command, Result<None>>(_authorization);
        }

        internal CancellationTokenSource Cancellation { get; } = new();
        internal DecisionSource Authorizer { get; } = new();
        internal OperationAuthorizationCoordinator Authorization => _authorization;
        internal AuthorizationEvidenceCoordinator Evidence => _evidence;
        internal ClockSource Clock { get; } = new();
        internal IOException RollbackFailure { get; } = new("Deliberate rollback transport failure.");
        internal int HandlerCalls { get; private set; }
        internal int FailCalls { get; private set; }
        internal int ClockReadsAtCleanup { get; private set; }
        internal bool WroteWhileTransactionActive { get; private set; }
        internal List<AuthorizationEvidenceOccurrence> Standalone { get; } = [];
        internal List<CancellationToken> StandaloneTokens { get; } = [];
        internal List<AuthorizationEvidenceOccurrence> Pending { get; } = [];

        internal Task<Result<None>> RunAsync(Error refusal, Exception? handlerException = null, string? transactionOperation = null,
            bool cancelBeforeTransactionObservation = false, Action? beforeResult = null)
        {
            var request = new Command();
            return _audit.Handle(request, () => _policy.Handle(request, () => transactionOperation switch
            {
                "ownerless" => Handler(),
                "direct" => _transactions.ExecuteAsync(null, TransactionMode.ReadWrite, "direct-cleanup-proof", Handler, Cancellation.Token),
                "different-request" => new TransactionBehavior<DifferentCommand, Result<None>>(_transactions,
                    BackgroundJobDefinitionRegistry.CreateEmpty(), new ExplicitReadOnlyRequestCatalog([]))
                    .Handle(new DifferentCommand(), Handler, Cancellation.Token),
                _ => new TransactionBehavior<Command, Result<None>>(_transactions,
                    BackgroundJobDefinitionRegistry.CreateEmpty(), new ExplicitReadOnlyRequestCatalog(transactionOperation == "read-only"
                        ? [new ExplicitReadOnlyRequest(typeof(Command))] : []))
                    .Handle(request, Handler, Cancellation.Token),
            }, Cancellation.Token), Cancellation.Token);

            Task<Result<None>> Handler()
            {
                HandlerCalls++;
                if (handlerException is not null) throw handlerException;
                beforeResult?.Invoke();
                var response = Task.FromResult(Result.Fail<None>(refusal));
                if (cancelBeforeTransactionObservation) Cancellation.Cancel();
                return response;
            }
        }

        public void Dispose()
        {
            _authorization.Dispose();
            _evidence.Dispose();
            _candidates.Dispose();
            _collector.Dispose();
            Cancellation.Dispose();
        }
    }

    private sealed class Catalog : IAuditCatalogSource
    {
        public string ModuleName => "fixture";
        public void Describe(IAuditCatalogBuilder builder) => builder.Off<Command>();
    }

    private sealed class PolicySource : IOperationPolicySource
    {
        private static readonly PermissionDefinition Permission = new(PermissionScope.Tenant, "fixture.cleanup.read");
        public IEnumerable<PermissionRegistration> Definitions => [new(Permission, true)];
        public IEnumerable<OperationPolicy> Policies => [OperationPolicy.ForProtected<Command>("fixture.cleanup.command", Permission)];
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        public AuthorizationCandidate ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            ForCandidate(AuthorizationSourceKind.Http, Proof.Actor, tenant, organization);
    }

    private sealed class DecisionSource : ProtectedOperationDecision.MintingSource, IProtectedOperationAuthorizer
    {
        private readonly Guid _membership = Guid.CreateVersion7();
        internal Guid Membership => _membership;
        internal int Calls { get; private set; }
        internal static ProtectedOperationDecision Recheck(OperationInvocation invocation, Guid membership) =>
            ForDecision(invocation, invocation.Candidate!, ProtectedOperationOutcome.Allowed, [membership]);
        public Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
            AuthorizationCandidate candidate, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, [_membership]));
        }
    }

    private sealed class ClockSource : IClock
    {
        internal DateTimeOffset Now { get; set; } = Proof.DecisionTime;
        internal int Reads { get; private set; }
        public DateTimeOffset UtcNow { get { Reads++; return Now; } }
    }
}

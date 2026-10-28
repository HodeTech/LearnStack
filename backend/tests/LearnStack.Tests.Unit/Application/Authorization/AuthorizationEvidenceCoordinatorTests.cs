using System.Data.Common;
using System.Net;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Authorization;

public sealed class AuthorizationEvidenceCoordinatorTests
{
    [Fact]
    public async Task Absorbed_denial_is_pending_on_the_exact_owner_then_only_its_acknowledged_commit_acknowledges_it()
    {
        using var proof = new Proof { Active = true };
        await using var execution = proof.Evidence.BeginExecution();
        Assert.True((await proof.AbsorbAsync()).IsSuccess);
        Assert.Empty(proof.Standalone);
        var batch = await proof.Evidence.BeforeOwnerCompletionAsync(proof.Unit, CancellationToken.None);
        Assert.Single(proof.Pending);
        Assert.Throws<InvalidOperationException>(() => proof.Evidence.RecordOwnerOutcome(batch, committed: true));
        proof.Active = false;
        proof.Evidence.RecordOwnerOutcome(batch, committed: true);
        proof.Evidence.RecordOwnerOutcome(batch, committed: true); // Same completion reference cannot repeat a write.
        using var cancelledAfterCommit = new CancellationTokenSource();
        cancelledAfterCommit.Cancel();
        await proof.Evidence.BeforeSuccessAsync(cancelledAfterCommit.Token);
        Assert.Empty(proof.Standalone);
        Assert.Equal(0, proof.Pending[0].Draft.Timestamp.Ticks % 10);
    }

    [Fact]
    public async Task Empty_completion_does_not_create_a_cancelled_write_after_success()
    {
        using var proof = new Proof();
        await using var execution = proof.Evidence.BeginExecution();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await proof.Evidence.BeforeSuccessAsync(cancelled.Token);
        Assert.Empty(proof.Standalone);
        Assert.Empty(proof.Pending);
    }

    [Fact]
    public async Task Pending_evidence_still_blocks_cancelled_success_until_an_actual_acknowledgement()
    {
        using var proof = new Proof { Active = true };
        await using var execution = proof.Evidence.BeginExecution();
        Assert.True((await proof.AbsorbAsync()).IsSuccess);
        proof.Active = false;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proof.Evidence.BeforeSuccessAsync(cancelled.Token));
        Assert.Empty(proof.Standalone);
        await proof.Evidence.BeforeSuccessAsync(CancellationToken.None);
        Assert.Single(proof.Standalone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_pending_evidence_requires_the_owners_tenant_announcement(bool pending)
    {
        using var proof = new Proof { Active = true };
        proof.Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction>()).Returns((TenantContextAnnouncement?)null);
        await using var execution = proof.Evidence.BeginExecution();
        if (pending)
        {
            Assert.True((await proof.AbsorbAsync()).IsSuccess);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                proof.Evidence.BeforeOwnerCompletionAsync(proof.Unit, CancellationToken.None));
        }
        else
        {
            Assert.Null(await proof.Evidence.BeforeOwnerCompletionAsync(proof.Unit, CancellationToken.None));
            proof.Unit.DidNotReceive().GetTenantContextAnnouncement(Arg.Any<DbTransaction>());
        }
        Assert.Empty(proof.Pending);
        Assert.Empty(proof.Standalone);
        proof.Active = false;
        await proof.Evidence.BeforeSuccessAsync(CancellationToken.None);
        Assert.Equal(pending ? 1 : 0, proof.Standalone.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_evidence_cannot_move_to_another_physical_transaction_or_reused_object_generation(bool reuseObject)
    {
        using var proof = new Proof { Active = true };
        await using var execution = proof.Evidence.BeginExecution();
        await proof.AbsorbAsync();
        proof.PendingAction = _ =>
        {
            if (reuseObject) proof.Generation++;
            else proof.CurrentTransaction = Substitute.For<DbTransaction>();
            return Task.CompletedTask;
        };
        var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(() =>
            proof.Evidence.BeforeOwnerCompletionAsync(proof.Unit, CancellationToken.None));
        Assert.Equal("audit_unavailable", failure.Error.Code);
        proof.Unit.Received(1).MarkRollbackOnly();
        proof.Active = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rolled_back_or_uncertain_business_state_reconciles_the_identical_denied_occurrence(bool uncertain)
    {
        using var proof = new Proof { Active = true };
        await using (proof.Evidence.BeginExecution())
        {
            await proof.AbsorbAsync();
            var batch = await proof.Evidence.BeforeOwnerCompletionAsync(proof.Unit, CancellationToken.None);
            proof.Active = false;
            if (uncertain) proof.Capture.MarkIndeterminate(new InvalidOperationException("fixture unknown COMMIT"));
            else proof.Capture.MarkRolledBack();
            proof.Evidence.RecordOwnerOutcome(batch, committed: false);
            await proof.Evidence.BeforeSuccessAsync(CancellationToken.None);
            Assert.Same(Assert.Single(proof.Pending), Assert.Single(proof.Standalone));
            Assert.Equal(AuditOutcome.Denied, proof.Standalone[0].Draft.Outcome);
            Assert.Equal(uncertain ? AuditIntentState.Indeterminate : AuditIntentState.RolledBack, proof.Capture.State);
        }
        Assert.Single(proof.Standalone);
    }

    [Fact]
    public async Task Ordinary_capture_committed_flag_cannot_suppress_an_unacknowledged_denial()
    {
        using var proof = new Proof { Active = true };
        await using var execution = proof.Evidence.BeginExecution();
        await proof.AbsorbAsync();
        proof.Capture.MarkCommitted();
        proof.Active = false;
        await proof.Evidence.BeforeSuccessAsync(CancellationToken.None);
        Assert.Single(proof.Standalone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readonly_or_ownerless_success_waits_for_standalone_acknowledgement(bool physicalReadonly)
    {
        using var proof = new Proof { Active = physicalReadonly, Mode = TransactionMode.ReadOnly };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proof.StandaloneAction = async _ => { entered.SetResult(); await release.Task; };
        await using var execution = proof.Evidence.BeginExecution();
        var running = proof.AbsorbAsync();
        if (physicalReadonly)
        {
            Assert.True((await running).IsSuccess);
            running = CompleteReadonlyAsync();
        }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(running.IsCompleted);
        release.SetResult();
        Assert.True((await running).IsSuccess);
        Assert.Single(proof.Standalone);
        Assert.Empty(proof.Pending);

        async Task<Result<None>> CompleteReadonlyAsync()
        {
            Assert.Null(await proof.Evidence.BeforeOwnerCompletionAsync(proof.Unit, CancellationToken.None));
            return Result.Ok(None.Value);
        }
    }

    [Fact]
    public async Task Failed_standalone_ack_blocks_absorbed_success_but_an_actual_refusal_keeps_403()
    {
        using (var refused = new Proof())
        {
            refused.StandaloneAction = _ => throw new AuditWriteFailedException("fixture evidence unavailable");
            var result = await refused.Owner.ExecuteRequestAsync<Denied, Result<None>>(new(), Proof.Never);
            Assert.Equal("forbidden", result.Error!.Code);
            Assert.Single(refused.Standalone);
        }
        using var absorbed = new Proof();
        absorbed.StandaloneAction = _ => throw new AuditWriteFailedException("fixture evidence unavailable");
        var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(() => absorbed.AbsorbAsync());
        Assert.Equal("audit_unavailable", failure.Error.Code);
        Assert.Single(absorbed.Standalone); // No finally retry loop after the actual failed acknowledgement.
    }

    [Fact]
    public async Task Same_decided_reference_is_one_event_while_repeated_actual_invocations_are_distinct()
    {
        using var proof = new Proof();
        proof.Authorizer.Created = decision =>
        {
            if (decision.Outcome != ProtectedOperationOutcome.Forbidden) return;
            proof.Evidence.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
            proof.Evidence.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
        };
        var request = new Denied();
        await proof.Owner.ExecuteRequestAsync<Denied, Result<None>>(request, Proof.Never);
        await proof.Owner.ExecuteRequestAsync<Denied, Result<None>>(request, Proof.Never);
        Assert.Equal(2, proof.Standalone.Count);
        Assert.NotEqual(proof.Standalone[0].Draft.Id, proof.Standalone[1].Draft.Id);
        Assert.All(proof.Standalone, occurrence => Assert.Equal(AuditOutcome.Denied, occurrence.Draft.Outcome));
    }

    [Fact]
    public async Task Resource_intents_keep_exact_nested_frame_policy_and_denial_ids_remain_bounded()
    {
        using var proof = new Proof { Active = true };
        proof.Capture.OpenFrame();
        proof.Capture.DeclareIntent(Proof.Intent("fixture.outer.read"));
        await proof.Owner.ExecuteRequestAsync<Allowed, Result<None>>(new(), async () =>
        {
            proof.Capture.OpenFrame();
            proof.Capture.DeclareIntent(Proof.Intent("fixture.inner.read"));
            var inner = await proof.Owner.ExecuteRequestAsync<Denied, Result<None>>(new(), Proof.Never);
            proof.Capture.CloseFrame(AuditIntentResult.Refused(AuditOutcome.Denied, inner.Error!.Message.Key));
            return Result.Ok(None.Value);
        });
        proof.Capture.CloseFrame(AuditIntentResult.Succeeded);
        Assert.Equal("fixture.outer", proof.Capture.Intents[0].Authorization!.PolicyId);
        Assert.Equal("fixture.inner", proof.Capture.Intents[1].Authorization!.PolicyId);
        Assert.Equal(new[] { proof.Authorizer.Membership }, proof.Capture.Intents[1].Authorization!.MembershipIds.ToArray());
    }

    [Fact]
    public async Task Composer_failure_has_one_fixed_critical_signal_and_prevents_success_without_logging_its_payload()
    {
        var transport = Substitute.For<IAuthorizationTransportSource>();
        transport.Capture(Arg.Any<OperationInvocation>()).Returns(_ => throw new InvalidOperationException("secret transport canary"));
        using var proof = new Proof([transport]);
        var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(() =>
            proof.Owner.ExecuteRequestAsync<Allowed, Result<None>>(new(), () => Task.FromResult(Result.Ok(None.Value))));
        Assert.Equal("audit_unavailable", failure.Error.Code);
        var logs = proof.Logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log").ToArray();
        var log = Assert.Single(logs);
        Assert.Equal(LogLevel.Critical, log.GetArguments()[0]);
        Assert.Null(log.GetArguments()[3]);
        Assert.DoesNotContain("canary", log.GetArguments()[2]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_inner_policy_without_a_resource_frame_cannot_replace_the_parent_intents_context()
    {
        using var proof = new Proof();
        proof.Capture.OpenFrame();
        proof.Capture.DeclareIntent(Proof.Intent("fixture.outer.read"));
        await proof.AbsorbAsync();
        proof.Capture.CloseFrame(AuditIntentResult.Succeeded);
        Assert.Equal("fixture.outer", Assert.Single(proof.Capture.Intents).Authorization!.PolicyId);
        Assert.Contains("fixture.inner", Assert.Single(proof.Standalone).Draft.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Later_resource_refusal_uses_the_actual_postlock_recheck_context_instead_of_initial_ids()
    {
        using var proof = new Proof();
        var postlockMember = Guid.CreateVersion7();
        await proof.Owner.ExecuteRequestAsync<Allowed, Result<None>>(new(), () =>
        {
            var decision = Evaluator.Recheck(proof.Owner.CurrentInvocation!, postlockMember);
            proof.Evidence.ReportRecheck(decision);
            return Task.FromResult(Result.Fail<None>(new Error(new LocalizedMessage("lockey_resource_scope_violation"))));
        });
        var metadata = Assert.Single(proof.Standalone).Draft.Metadata!;
        Assert.Contains(postlockMember.ToString("D"), metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.Authorizer.Membership.ToString("D"), metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Descriptive_transport_is_sanitized_frozen_and_kept_outside_metadata_payloads()
    {
        using var transport = new TransportSource();
        using var proof = new Proof([transport]);
        proof.Capture.OpenFrame();
        proof.Capture.DeclareIntent(Proof.Intent("fixture.outer.read"));
        await proof.Owner.ExecuteRequestAsync<Allowed, Result<None>>(new(), () => Task.FromResult(Result.Ok(None.Value)));
        proof.Capture.CloseFrame(AuditIntentResult.Succeeded);
        var intent = Assert.Single(proof.Capture.Intents);
        var address = intent.Authorization!.Address!;
        address.ScopeId = 42;
        var draft = AuditDraftComposer.InTransaction(intent, []);
        Assert.Equal(0, draft.IpAddress!.ScopeId);
        Assert.Equal(256, draft.UserAgent!.Length);
        Assert.DoesNotContain(draft.UserAgent, char.IsControl);
        Assert.Contains("FixedRoute", draft.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-canary", draft.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("fe80", draft.Metadata, StringComparison.Ordinal);
        Assert.Null(draft.ActorEmail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_assertion_reference_is_fixed_once_and_preserves_refusal_on_write_loss(bool fail)
    {
        using var proof = new Proof();
        using var source = new ClaimSource();
        if (fail) proof.StandaloneAction = _ => throw new AuditWriteFailedException("fixture evidence loss");
        var assertion = source.Mint();
        await proof.Evidence.RecordConfirmedAssertionAsync(assertion);
        await proof.Evidence.RecordConfirmedAssertionAsync(assertion);
        var occurrence = Assert.Single(proof.Standalone);
        Assert.Equal(AuthorizationEvidenceKind.ClaimAssertion, occurrence.Kind);
        Assert.Equal("tenancy.assertion.reject_claim_context", occurrence.Draft.Operation);
        Assert.Equal(CandidateSource.Tenant, occurrence.Draft.TenantId);
        Assert.Contains("membership_confirmed_header", occurrence.Draft.Metadata, StringComparison.Ordinal);
        Assert.Contains("\"dimension\":\"organization\"", occurrence.Draft.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("asserted", occurrence.Draft.Metadata, StringComparison.Ordinal);
        Assert.Equal(0, occurrence.Draft.Timestamp.Ticks % 10);
    }

    private sealed record Allowed : IRequest<Result<None>>;
    private sealed record Denied : IRequest<Result<None>>;

    private sealed class Proof : IDisposable
    {
        private readonly CandidateSource _candidate = new();
        public Proof(IAuthorizationTransportSource[]? transports = null)
        {
            Unit = Substitute.For<IUnitOfWork>();
            CurrentTransaction = Substitute.For<DbTransaction>();
            Unit.HasActiveTransaction.Returns(_ => Active);
            Unit.Mode.Returns(_ => Active ? Mode : null);
            Unit.Transaction.Returns(_ => Active ? CurrentTransaction : null);
            Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction>()).Returns(call =>
                ReferenceEquals(call.Arg<DbTransaction>(), CurrentTransaction)
                    ? new TenantContextAnnouncement(Generation, CandidateSource.Tenant, null) : null);
            var store = Substitute.For<IAuditStore>();
            store.WriteAuthorizationPendingAsync(Unit, Arg.Any<AuthorizationEvidenceOccurrence>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var occurrence = call.Arg<AuthorizationEvidenceOccurrence>();
                    Pending.Add(occurrence);
                    return PendingAction(occurrence);
                });
            store.WriteAuthorizationStandaloneAsync(Arg.Any<AuthorizationEvidenceOccurrence>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var occurrence = call.Arg<AuthorizationEvidenceOccurrence>();
                    Standalone.Add(occurrence);
                    return StandaloneAction(occurrence);
                });
            Logger.IsEnabled(LogLevel.Critical).Returns(true);
            var context = TenantContextFactory.Create(new()
            {
                HasValidatedPrincipal = true,
                ClaimTenantId = CandidateSource.Tenant,
                UserId = CandidateSource.Actor,
                MembershipCovers = true,
            }).Value!;
            Evidence = new(store, Capture, Unit, new SystemClock(), new SystemGuidFactory(), () => Owner!,
                transports ?? [], Logger);
            Owner = new(new OperationPolicyCatalog([new TestPolicySource()]), Authorizer, context, [_candidate], [], Evidence);
        }
        public IUnitOfWork Unit { get; }
        public AuditStateCapture Capture { get; } = new();
        public Evaluator Authorizer { get; } = new();
        public ILogger<AuthorizationEvidenceCoordinator> Logger { get; } = Substitute.For<ILogger<AuthorizationEvidenceCoordinator>>();
        public AuthorizationEvidenceCoordinator Evidence { get; }
        public OperationAuthorizationCoordinator Owner { get; }
        public bool Active { get; set; }
        public DbTransaction CurrentTransaction { get; set; }
        public long Generation { get; set; } = 1;
        public TransactionMode Mode { get; set; } = TransactionMode.ReadWrite;
        public List<AuthorizationEvidenceOccurrence> Pending { get; } = [];
        public List<AuthorizationEvidenceOccurrence> Standalone { get; } = [];
        public Func<AuthorizationEvidenceOccurrence, Task> StandaloneAction { get; set; } = _ => Task.CompletedTask;
        public Func<AuthorizationEvidenceOccurrence, Task> PendingAction { get; set; } = _ => Task.CompletedTask;
        public Task<Result<None>> AbsorbAsync() => Owner.ExecuteRequestAsync<Allowed, Result<None>>(new(), async () =>
        {
            Assert.Equal("forbidden", (await Owner.ExecuteRequestAsync<Denied, Result<None>>(new(), Never)).Error!.Code);
            return Result.Ok(None.Value);
        });
        public static AuditIntent Intent(string operation) => new(AuditEntryId.From(Guid.CreateVersion7()), CandidateSource.Tenant,
            null, CandidateSource.Actor, null, "fixture", operation, OperationType.ReadSensitive, OperationClass.Must, null, DateTimeOffset.UtcNow);
        public static Task<Result<None>> Never() => throw new InvalidOperationException("The denied handler must not run.");
        public void Dispose() { Owner.Dispose(); Evidence.Dispose(); _candidate.Dispose(); }
    }

    private sealed class TestPolicySource : IOperationPolicySource
    {
        private static readonly PermissionDefinition Pair = new(PermissionScope.Tenant, "identity.example.read");
        public IEnumerable<PermissionRegistration> Definitions => [new(Pair, true)];
        public IEnumerable<OperationPolicy> Policies =>
        [OperationPolicy.ForProtected<Allowed>("fixture.outer", Pair), OperationPolicy.ForProtected<Denied>("fixture.inner", Pair)];
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        internal static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
        internal static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
        public AuthorizationCandidate ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            ForCandidate(AuthorizationSourceKind.Http, Actor, tenant, organization);
    }

    private sealed class Evaluator : ProtectedOperationDecision.MintingSource, IProtectedOperationAuthorizer
    {
        public Guid Membership { get; } = Guid.CreateVersion7();
        public Action<ProtectedOperationDecision>? Created { get; set; }
        public static ProtectedOperationDecision Recheck(OperationInvocation invocation, Guid membership) =>
            ForDecision(invocation, invocation.Candidate!, ProtectedOperationOutcome.Allowed, [membership]);
        public Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
            AuthorizationCandidate candidate, CancellationToken cancellationToken = default)
        {
            var decision = ForDecision(invocation, candidate, policy.RequestType == typeof(Denied)
                ? ProtectedOperationOutcome.Forbidden : ProtectedOperationOutcome.Allowed, [Membership]);
            Created?.Invoke(decision);
            return Task.FromResult(decision);
        }
    }

    private sealed class TransportSource : OperationTransportProvenance.MintingSource, IAuthorizationTransportSource
    {
        public OperationTransportProvenance Capture(OperationInvocation invocation) => ForTransport(invocation, "FixedRoute",
            IPAddress.Parse("fe80::1"), "agent-canary\r\n\0" + new string('x', 300));
    }

    private sealed class ClaimSource : ConfirmedClaimAssertion.MintingSource
    {
        public ConfirmedClaimAssertion Mint() => ForAssertion(TenantContextFactory.Create(new()
        {
            HasValidatedPrincipal = true,
            ClaimTenantId = CandidateSource.Tenant,
            UserId = CandidateSource.Actor,
            MembershipCovers = true,
        }).Value!, ClaimAssertionDimension.Organization, "FixedRoute", null, null);
    }
}

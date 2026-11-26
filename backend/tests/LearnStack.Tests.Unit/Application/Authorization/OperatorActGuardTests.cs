using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Authorization;

/// <summary>
/// The handler-side check of an operator act (ADR-0102 § 1, § 7): it admits the live act of the
/// right source in the right context and transaction, and refuses everything else before a lock is
/// taken or a row is written.
/// </summary>
/// <remarks>
/// The guard is driven through the real authorization coordinator, so the invocation it reads is the
/// one the pipeline would have built. Only the source is a double: it mints the operator descriptor
/// the executor's source mints, and, for the cross-source case, a seed descriptor.
/// </remarks>
public sealed class OperatorActGuardTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static OperatorProvisionTenantCommand Request() => new(
        Tenant, "acme", "Acme", OrganizationId.From(Guid.CreateVersion7()), "main", "Main");

    private static IdentityMutationLockPlan Plan() => new([], Tenant, IdentityTenantLockMode.Exclusive);

    [Fact]
    public async Task The_Live_Act_In_Its_Own_Context_Is_Admitted_And_Holds_Its_Locks()
    {
        using var rig = new Rig();
        var request = Request();

        var result = await rig.RunAsync(request, async () =>
        {
            var admission = await rig.Guard.AdmitAsync(request, Plan(), default);
            admission.IsSuccess.Should().BeTrue();
            rig.Guard.RequireCurrent(admission.Value!).IsSuccess.Should().BeTrue();
            admission.Value!.TenantId.Should().Be(Tenant);
            return Result.Ok(None.Value);
        });

        result.IsSuccess.Should().BeTrue();
        await rig.Locks.Received(1).AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Call_Outside_Any_Invocation_Is_Refused_Before_A_Lock()
    {
        using var rig = new Rig();

        var result = await rig.Guard.AdmitAsync(Request(), Plan(), default);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("forbidden");
        await rig.Locks.DidNotReceive().AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Request_That_Is_Not_The_Invocations_Own_Object_Is_Refused()
    {
        using var rig = new Rig();

        await rig.ExpectRefusalAsync(Request(), () => Request());
    }

    [Fact]
    public async Task A_Seed_Source_Is_Not_Sufficient_For_An_Operator_Guard()
    {
        using var rig = new Rig();
        var seed = new ProvisionTenantCommand(Tenant, "acme", "Acme", OrganizationId.From(Guid.CreateVersion7()), "main", "Main");
        rig.SourceKind = AuthorizationSourceKind.Seed;

        var result = await rig.RunAsync(seed, async () =>
        {
            var admission = await rig.Guard.AdmitAsync(seed, Plan(), default);
            admission.IsFailure.Should().BeTrue("the guard names the operator source alone");
            admission.Error!.Code.Should().Be("forbidden");
            return Result.Ok(None.Value);
        });

        result.IsSuccess.Should().BeTrue();
        await rig.Locks.DidNotReceive().AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Revoked_Source_Admits_Nothing()
    {
        using var rig = new Rig();
        var request = Request();

        await rig.RunAsync(request, async () =>
        {
            rig.Source.Dispose();
            var admission = await rig.Guard.AdmitAsync(request, Plan(), default);
            admission.IsFailure.Should().BeTrue();
            return Result.Ok(None.Value);
        });

        await rig.Locks.DidNotReceive().AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("organization")]
    [InlineData("host")]
    [InlineData("unresolved")]
    [InlineData("other_tenant")]
    public async Task A_Context_That_Is_Not_The_Tenants_Ambient_One_Is_Refused(string change)
    {
        // Applied before the invocation starts, so the invocation's own drift check has nothing to catch and
        // the guard's restatement of the context is the only thing that can refuse.
        using var rig = new Rig();
        var request = Request();
        Apply(rig, change);

        await rig.ExpectRefusalAsync(request, () => request);
    }

    private static void Apply(Rig rig, string change)
    {
        switch (change)
        {
            case "user": rig.Context.UserId.Returns(UserId.SystemActor); break;
            case "organization": rig.Context.OrganizationId.Returns(OrganizationId.From(Guid.CreateVersion7())); break;
            case "host":
                rig.Context.HostScope.Returns(TenantContextFactory.Create(
                    new TenantResolutionAttempt { HostTenantId = Tenant }).Value!.HostScope);
                break;
            case "unresolved": rig.Context.IsResolved.Returns(false); break;
            default: rig.Context.TenantId.Returns(TenantId.From(Guid.CreateVersion7())); break;
        }
    }

    [Theory]
    [InlineData("no_transaction")]
    [InlineData("read_only")]
    [InlineData("rollback_only")]
    [InlineData("announced_elsewhere")]
    [InlineData("not_announced")]
    [InlineData("global_identity")]
    [InlineData("announced_with_an_organization")]
    public async Task A_Transaction_That_Is_Not_The_Ordinary_Announced_Owner_Is_Refused(string change)
    {
        using var rig = new Rig();
        var request = Request();

        await rig.ExpectRefusalAsync(request, () =>
        {
            switch (change)
            {
                case "no_transaction": rig.Unit.HasActiveTransaction.Returns(false); break;
                case "read_only": rig.Unit.Mode.Returns(TransactionMode.ReadOnly); break;
                case "rollback_only": rig.Unit.IsRollbackOnly.Returns(true); break;
                case "announced_elsewhere":
                    rig.Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>())
                        .Returns(new TenantContextAnnouncement(1, TenantId.From(Guid.CreateVersion7()), null));
                    break;
                case "not_announced": rig.Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>()).Returns((TenantContextAnnouncement?)null); break;
                case "announced_with_an_organization":
                    // The tenant's ambient context with no organization, on a transaction that announced one: not its owner.
                    rig.Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>())
                        .Returns(new TenantContextAnnouncement(1, Tenant, OrganizationId.From(Guid.CreateVersion7())));
                    break;
                default: rig.GlobalUnit.IsGlobalIdentityTransaction.Returns(true); break;
            }

            return request;
        });
    }

    [Fact]
    public async Task A_Lock_That_Cannot_Be_Taken_Is_Unavailable_And_Rolls_The_Act_Back()
    {
        using var rig = new Rig();
        var request = Request();
        rig.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IdentityMutationLockException()));

        await rig.RunAsync(request, async () =>
        {
            var admission = await rig.Guard.AdmitAsync(request, Plan(), default);
            admission.IsFailure.Should().BeTrue();
            admission.Error!.Code.Should().Be("dependency_unavailable");
            return Result.Ok(None.Value);
        });

        rig.Unit.Received(1).MarkRollbackOnly();
    }

    [Fact]
    public async Task A_Lock_That_Was_Lost_Before_A_Write_Refuses_The_Write_And_Rolls_The_Act_Back()
    {
        using var rig = new Rig();
        var request = Request();

        await rig.RunAsync(request, async () =>
        {
            var admission = await rig.Guard.AdmitAsync(request, Plan(), default);
            admission.IsSuccess.Should().BeTrue();
            rig.Locks.When(value => value.RequireCurrent(Arg.Any<IdentityMutationLockPlan>()))
                .Do(_ => throw new IdentityMutationLockException());

            var current = rig.Guard.RequireCurrent(admission.Value!);

            current.IsFailure.Should().BeTrue();
            current.Error!.Code.Should().Be("dependency_unavailable");
            return Result.Ok(None.Value);
        });

        rig.Unit.Received(1).MarkRollbackOnly();
    }

    [Fact]
    public async Task The_Live_Act_Check_Answers_Without_Taking_A_Lock()
    {
        using var rig = new Rig();
        var request = Request();

        await rig.RunAsync(request, () =>
        {
            rig.Guard.RequireLiveAct(request).IsSuccess.Should().BeTrue();
            rig.Guard.RequireLiveAct(Request()).IsFailure.Should().BeTrue("another request object is not this invocation's");
            return Task.FromResult(Result.Ok(None.Value));
        });

        rig.Guard.RequireLiveAct(request).IsFailure.Should().BeTrue("outside an invocation there is no live act");
        await rig.Locks.DidNotReceive().AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("organization")]
    [InlineData("host")]
    [InlineData("unresolved")]
    public async Task The_Live_Act_Check_Refuses_A_Context_That_Is_Not_The_Tenants_Ambient_One(string change)
    {
        using var rig = new Rig();
        var request = Request();
        Apply(rig, change);
        var refused = false;

        await rig.RunAsync(request, () =>
        {
            refused = rig.Guard.RequireLiveAct(request).IsFailure;
            return Task.FromResult(Result.Ok(None.Value));
        });

        refused.Should().BeTrue();
    }

    [Fact]
    public void A_Replay_Is_Recorded_On_The_Guard_And_Starts_Unrecorded()
    {
        using var rig = new Rig();

        rig.Guard.Replayed.Should().BeFalse();
        rig.Guard.MarkReplayed();
        rig.Guard.Replayed.Should().BeTrue();
    }

    [Theory]
    [InlineData(AuthorizationSourceKind.PlatformOperator, false)]
    [InlineData(AuthorizationSourceKind.Seed, true)]
    public async Task An_Authorization_Refusal_Of_An_Operator_Act_Writes_No_Durable_Denial_Row_Unlike_Any_Other_Source(
        AuthorizationSourceKind source, bool writes)
    {
        // ADR-0033 Amendment 14. The control is the seed source, which records the same refusal, so the empty
        // answer for the operator is the coordinator's decision and not a fixture that cannot write.
        using var rig = new Rig { SourceKind = source };
        OperatorProvisionTenantCommand operatorRequest = Request();
        var seedRequest = new ProvisionTenantCommand(Tenant, "acme", "Acme", OrganizationId.From(Guid.CreateVersion7()), "main", "Main");
        await using var execution = rig.Evidence.BeginExecution();

        Task<Result<None>> Refuse(object request) => rig.RunAsync(request, () =>
        {
            rig.Evidence.RecordPolicyRefusal(rig.CurrentInvocation!, null, new Error(new LocalizedMessage("lockey_forbidden")));
            return Task.FromResult(Result.Fail<None>(new Error(new LocalizedMessage("lockey_forbidden"))));
        });

        await (source == AuthorizationSourceKind.PlatformOperator ? Refuse(operatorRequest) : Refuse(seedRequest));
        await rig.Evidence.BeforeOwnerCompletionAsync(rig.Unit, CancellationToken.None);
        await rig.Evidence.BeforeSuccessAsync(CancellationToken.None);

        (rig.EvidenceWrites().Length > 0).Should().Be(writes);
    }

    /// <summary>
    /// ADR-0102 Amendment 2: a closure's fan-out runs in its participant group's organization. The guard admits a context
    /// that names an organization only for that operation, by reference, and only on a transaction that announced the same
    /// tenant and organization; the tenant-wide group of the same act announces none.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Closure_Fan_Out_Is_Admitted_In_Its_Announced_Group(bool organization)
    {
        using var rig = new Rig { Operation = PlatformOperations.ClosureFanOut };
        var group = organization ? OrganizationId.From(Guid.CreateVersion7()) : (OrganizationId?)null;
        rig.Announce(group);
        var request = new FanOutClosureCommand(UserId.From(Guid.CreateVersion7()), group);

        await rig.RunAsync(request, async () =>
        {
            rig.Guard.RequireLiveAct(request).IsSuccess.Should().BeTrue();
            var admission = await rig.Guard.AdmitAsync(request, Plan(), default);
            admission.IsSuccess.Should().BeTrue();
            rig.Guard.RequireCurrent(admission.Value!).IsSuccess.Should().BeTrue();
            return Result.Ok(None.Value);
        });

        await rig.Locks.Received(1).AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The organization's admission is the fan-out's alone, and only for the group its transaction announced: an onboarding
    /// act in an organization context, and a fan-out whose transaction announced another organization or none, are each
    /// refused before a lock.
    /// </summary>
    [Theory]
    [InlineData("onboarding_operation")]
    [InlineData("announced_another_organization")]
    [InlineData("announced_no_organization")]
    public async Task An_Organization_Is_Admitted_Only_For_The_Fan_Out_On_Its_Own_Announcement(string variant)
    {
        using var rig = new Rig
        {
            Operation = variant == "onboarding_operation" ? PlatformOperations.TenantProvision : PlatformOperations.ClosureFanOut,
        };
        var group = OrganizationId.From(Guid.CreateVersion7());
        rig.Announce(group);
        if (variant == "announced_another_organization")
            rig.Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>())
                .Returns(new TenantContextAnnouncement(1, Tenant, OrganizationId.From(Guid.CreateVersion7())));
        else if (variant == "announced_no_organization")
            rig.Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>()).Returns(new TenantContextAnnouncement(1, Tenant, null));
        var request = new FanOutClosureCommand(UserId.From(Guid.CreateVersion7()), group);
        var live = true;

        await rig.RunAsync(request, () =>
        {
            live = rig.Guard.RequireLiveAct(request).IsSuccess;
            return Task.FromResult(Result.Ok(None.Value));
        });
        await rig.ExpectRefusalAsync(request, () => request);

        live.Should().BeFalse();
    }

    [Fact]
    public async Task A_Plan_For_Another_Tenant_Is_Not_Held_For_This_Act()
    {
        using var rig = new Rig();
        var request = Request();

        await rig.RunAsync(request, async () =>
        {
            var admission = await rig.Guard.AdmitAsync(
                request, new IdentityMutationLockPlan([], TenantId.From(Guid.CreateVersion7()), IdentityTenantLockMode.Exclusive), default);
            admission.IsFailure.Should().BeTrue("the act may lock only the tenant its context carries");
            return Result.Ok(None.Value);
        });

        await rig.Locks.DidNotReceive().AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
    }

    private sealed class Rig : IDisposable
    {
        private readonly OperationAuthorizationCoordinator _coordinator;
        private IdentityMutationLockPlan? _held;

        public Rig()
        {
            Context.IsResolved.Returns(true);
            Context.TenantId.Returns(Tenant);
            Context.Origin.Returns(TenantContextOrigin.Ambient);
            var transaction = Substitute.For<DbTransaction>();
            Unit.HasActiveTransaction.Returns(true);
            Unit.Mode.Returns(TransactionMode.ReadWrite);
            Unit.Transaction.Returns(transaction);
            Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>()).Returns(new TenantContextAnnouncement(1, Tenant, null));
            Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                _held = call.Arg<IdentityMutationLockPlan>();
                return Task.CompletedTask;
            });
            Locks.When(value => value.RequireCurrent(Arg.Any<IdentityMutationLockPlan>())).Do(call =>
            {
                if (!ReferenceEquals(_held, call.Arg<IdentityMutationLockPlan>())) throw new IdentityMutationLockException();
            });

            Source = new OperatorSource(this);
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);
            OperationAuthorizationCoordinator? current = null;
            Evidence = new AuthorizationEvidenceCoordinator(
                Store, new AuditStateCapture(), Unit, clock, new SystemGuidFactory(),
                () => current!, [], NullLogger<AuthorizationEvidenceCoordinator>.Instance);
            _coordinator = current = new OperationAuthorizationCoordinator(
                new OperationPolicyCatalog([new TenancyOperationPolicySource(), new IdentityOperationPolicySource()]),
                Substitute.For<IProtectedOperationAuthorizer>(), Context, [], [Source], Evidence);
            Guard = new OperatorActGuard(_coordinator, Context, Unit, Locks);
        }

        public ITenantContext Context { get; } = Substitute.For<ITenantContext>();

        public IAuditStore Store { get; } = Substitute.For<IAuditStore>();

        public AuthorizationEvidenceCoordinator Evidence { get; }

        public OperationInvocation? CurrentInvocation => _coordinator.CurrentInvocation;

        /// <summary>The authorization-evidence writes the store was asked for, by method name.</summary>
        public string[] EvidenceWrites() => [.. Store.ReceivedCalls()
            .Select(call => call.GetMethodInfo().Name).Where(name => name.StartsWith("WriteAuthorization", StringComparison.Ordinal))];

        public IGlobalIdentityUnitOfWork GlobalUnit { get; } = Substitute.For<IGlobalIdentityUnitOfWork>();

        public IUnitOfWork Unit => GlobalUnit;

        public IIdentityMutationLocks Locks { get; } = Substitute.For<IIdentityMutationLocks>();

        public OperatorActGuard Guard { get; }

        public OperatorSource Source { get; }

        public AuthorizationSourceKind SourceKind { get; set; } = AuthorizationSourceKind.PlatformOperator;

        /// <summary>The operation the operator descriptor is minted for.</summary>
        public PlatformOperation Operation { get; set; } = PlatformOperations.TenantProvision;

        /// <summary>The context and its transaction's announcement, for <paramref name="organization"/>'s group of the tenant.</summary>
        public void Announce(OrganizationId? organization)
        {
            Context.OrganizationId.Returns(organization);
            Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>()).Returns(new TenantContextAnnouncement(1, Tenant, organization));
        }

        public Task<Result<None>> RunAsync<TRequest>(TRequest request, Func<Task<Result<None>>> body) where TRequest : notnull =>
            _coordinator.ExecuteRequestAsync<TRequest, Result<None>>(request, body);

        /// <summary>Runs the guard after <paramref name="change"/> altered the world, and expects a refusal and no lock.</summary>
        public async Task ExpectRefusalAsync<TRequest>(TRequest request, Func<TRequest> change) where TRequest : notnull
        {
            var refused = false;
            await RunAsync(request, async () =>
            {
                var asked = change();
                var admission = await Guard.AdmitAsync(asked, Plan(), default);
                refused = admission.IsFailure && admission.Error!.Code == "forbidden";
                return Result.Ok(None.Value);
            });

            refused.Should().BeTrue();
            await Locks.DidNotReceive().AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
        }

        public void Dispose() => _coordinator.Dispose();
    }

    private sealed class OperatorSource(Rig rig) : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
    {
        public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context)
        {
            if (!CanIssue)
            {
                return null;
            }

            return rig.SourceKind == AuthorizationSourceKind.PlatformOperator
                ? ForOperation(
                    AuthorizationSourceKind.PlatformOperator, requestType, context, null,
                    PlatformOperatorAttribution.From(TestPlatformAdmissions.Admission(rig.Operation, issuedAt: Now)))
                : ForOperation(AuthorizationSourceKind.Seed, requestType, context);
        }

        protected override bool IsEvidenceCurrent(object? evidence) => true;
    }
}

using FluentAssertions;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// ADR-0107 § 7: what the closure's fan-out decides around each group's act, with the real operator act executor and a
/// sender double in place of the pipeline. One admission per group from the gate; groups in one deterministic order; every
/// group answered, a refused admission, a refusal and an exception each that group's failure; only the operator's
/// cancellation stops the loop; and a request that is not a closing person's bounded set of distinct groups is refused
/// before any act.
/// </summary>
public sealed class ClosureFanOutUnitTests
{
    private static readonly UserId Person = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId First = TenantId.From(Guid.Parse("01930000-0000-7000-8000-000000000001"));
    private static readonly TenantId Second = TenantId.From(Guid.Parse("01930000-0000-7000-8000-000000000002"));
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-0000000000b1"));
    private static readonly OrganizationId OtherBranch = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-0000000000b2"));

    [Fact]
    public async Task Every_group_is_one_admitted_act_in_its_own_tenant_and_organization_in_a_deterministic_order()
    {
        using var rig = new Rig();
        // Listed out of order on purpose: the fan-out sorts by tenant, the tenant-wide group first, then each organization.
        ClosureParticipantGroup[] groups = [new(Second, null), new(First, OtherBranch), new(First, null), new(First, Branch)];

        var reports = await rig.FanOut.FanOutAsync(TestPlatformAdmissions.Witness(), "CHG-107", new ClosureFanOutRequest(Person, groups));

        reports.Select(report => report.Group).Should().Equal(new ClosureParticipantGroup(First, null), new ClosureParticipantGroup(First, Branch),
            new ClosureParticipantGroup(First, OtherBranch), new ClosureParticipantGroup(Second, null));
        reports.Should().OnlyContain(report => report.Outcome == ClosureGroupOutcome.Started);
        rig.Acts.Select(act => (act.Tenant, act.Organization, act.Command.OrganizationId)).Should().Equal(
            (First, (OrganizationId?)null, (OrganizationId?)null), (First, Branch, Branch), (First, OtherBranch, OtherBranch),
            (Second, null, null));
        rig.Acts.Should().OnlyContain(act => act.Command.SubjectUserId == Person && act.Operation == PlatformOperations.ClosureFanOut);
#pragma warning disable CA2012 // NSubstitute checks the call; nothing is awaited.
        _ = rig.Gate.Received(4).AdmitAsync(Arg.Any<PlatformOperatorWitness>(), PlatformOperations.ClosureFanOut, "CHG-107",
            Arg.Any<CancellationToken>());
#pragma warning restore CA2012
    }

    [Fact]
    public async Task A_refused_admission_a_refusal_and_an_exception_are_each_their_groups_failure_and_the_others_still_run()
    {
        using var rig = new Rig();
        rig.RefuseAdmissionAt.Add(1); // the second group in the fan-out's order: (First, Branch)
        rig.Refuse.Add(new(First, OtherBranch));
        rig.Throw.Add(new(Second, null));
        rig.Answer[new(First, null)] = ClosureGroupOutcome.Adopted;
        ClosureParticipantGroup[] groups = [new(First, null), new(First, Branch), new(First, OtherBranch), new(Second, null)];

        var reports = await rig.FanOut.FanOutAsync(TestPlatformAdmissions.Witness(), null, new ClosureFanOutRequest(Person, groups));

        reports.Select(report => report.Outcome).Should().Equal(ClosureGroupOutcome.Adopted, ClosureGroupOutcome.Failed,
            ClosureGroupOutcome.Failed, ClosureGroupOutcome.Failed);
        rig.Acts.Select(act => act.Organization).Should().Equal([null, OtherBranch, null],
            "the refused admission ran no act; the refusal and the exception each ran theirs");
    }

    [Fact]
    public async Task The_operators_cancellation_stops_the_loop_by_throwing()
    {
        using var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        rig.OnAct = () => cancellation.Cancel();
        ClosureParticipantGroup[] groups = [new(First, null), new(First, Branch)];

        var act = () => rig.FanOut.FanOutAsync(TestPlatformAdmissions.Witness(), null, new ClosureFanOutRequest(Person, groups), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Acts.Should().ContainSingle("the second group is never started once the operator cancelled");
    }

    /// <summary>
    /// Exactly the closure's group bound, which Begin admits, is fanned out in one call: every group is one act. The bound is
    /// the shared constant Begin judges, so no person Begin admits is refused here.
    /// </summary>
    [Fact]
    public async Task Exactly_the_closure_s_group_bound_is_fanned_out_in_one_call()
    {
        using var rig = new Rig();
        ClosureFanOut.MaximumGroups.Should().Be(LearnStack.SharedKernel.DataProtection.ClosureLimits.MaximumParticipantGroups);
        var groups = Enumerable.Range(0, ClosureFanOut.MaximumGroups)
            .Select(_ => new ClosureParticipantGroup(TenantId.From(Guid.CreateVersion7()), null)).ToArray();

        var reports = await rig.FanOut.FanOutAsync(TestPlatformAdmissions.Witness(), null, new ClosureFanOutRequest(Person, groups));

        reports.Should().HaveCount(ClosureFanOut.MaximumGroups).And.OnlyContain(report => report.Outcome == ClosureGroupOutcome.Started);
        rig.Acts.Should().HaveCount(ClosureFanOut.MaximumGroups);
    }

    [Theory]
    [InlineData("system_actor")]
    [InlineData("duplicate_group")]
    [InlineData("sentinel_tenant")]
    [InlineData("too_many_groups")]
    public async Task A_request_that_is_not_a_closing_persons_bounded_distinct_groups_is_refused_before_any_act(string variant)
    {
        using var rig = new Rig();
        var request = variant switch
        {
            "system_actor" => new ClosureFanOutRequest(UserId.SystemActor, [new(First, null)]),
            "duplicate_group" => new ClosureFanOutRequest(Person, [new(First, Branch), new(First, Branch)]),
            "sentinel_tenant" => new ClosureFanOutRequest(Person, [new(TenantId.PlatformSentinel, null)]),
            _ => new ClosureFanOutRequest(Person, Enumerable.Range(0, ClosureFanOut.MaximumGroups + 1)
                .Select(_ => new ClosureParticipantGroup(TenantId.From(Guid.CreateVersion7()), null)).ToArray()),
        };

        var act = () => rig.FanOut.FanOutAsync(TestPlatformAdmissions.Witness(), null, request);

        await act.Should().ThrowAsync<ArgumentException>();
        rig.Acts.Should().BeEmpty();
#pragma warning disable CA2012 // NSubstitute checks the call; nothing is awaited.
        _ = rig.Gate.DidNotReceiveWithAnyArgs().AdmitAsync(default!, default!, default, default);
#pragma warning restore CA2012
    }

    private sealed record Act(PlatformOperation Operation, TenantId Tenant, OrganizationId? Organization, FanOutClosureCommand Command);

    private sealed class Rig : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        private readonly ServiceProvider _provider;
        private int _admissions;

        public Rig()
        {
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);
#pragma warning disable CA2012 // NSubstitute configures the call; each execution returns a fresh ValueTask.
            Gate.AdmitAsync(Arg.Any<PlatformOperatorWitness>(), Arg.Any<PlatformOperation>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call => ValueTask.FromResult(RefuseAdmissionAt.Contains(_admissions++)
                    ? PlatformAdmissionResult.Refuse(PlatformRefusal.MissingPermission)
                    : PlatformAdmissionResult.Admit(TestPlatformAdmissions.Admission(call.Arg<PlatformOperation>(), call.Arg<string?>(),
                        issuedAt: Now))));
#pragma warning restore CA2012
            var services = new ServiceCollection();
            services.AddSingleton<ITenantContextAccessor>(Accessor);
            services.AddSingleton<IOperationPolicyCatalog>(
                new OperationPolicyCatalog([new TenancyOperationPolicySource(), new IdentityOperationPolicySource()]));
            services.AddSingleton(clock);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddScoped(_ => new OperatorActGuard(
                Substitute.For<IOperationInvocationContext>(), Substitute.For<ITenantContext>(),
                Substitute.For<IUnitOfWork>(), Substitute.For<IIdentityMutationLocks>()));
            services.AddPlatformOperatorActs(new PlatformOperatorActCatalog(
                [new(typeof(FanOutClosureCommand), new(PlatformOperations.ClosureFanOut))]));
            services.AddScoped(_ =>
            {
                var sender = Substitute.For<ISender>();
                sender.Send(Arg.Any<IRequest<Result<ClosureGroupOutcome>>>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    var context = Accessor.Current!;
                    var command = (FanOutClosureCommand)call.Arg<IRequest<Result<ClosureGroupOutcome>>>();
                    var group = new ClosureParticipantGroup(context.TenantId, context.OrganizationId);
                    Acts.Add(new Act(PlatformOperations.ClosureFanOut, context.TenantId, context.OrganizationId, command));
                    OnAct?.Invoke();
                    if (Throw.Contains(group))
                        return Task.FromException<Result<ClosureGroupOutcome>>(new InvalidOperationException("act failed"));
                    return Task.FromResult(Refuse.Contains(group)
                        ? Result<ClosureGroupOutcome>.Fail(new Error(new LocalizedMessage("lockey_not_found")))
                        : Result.Ok(Answer.GetValueOrDefault(group, ClosureGroupOutcome.Started)));
                });
                return sender;
            });
            _provider = services.BuildServiceProvider();
            FanOut = new ClosureFanOut(Gate, _provider.GetRequiredService<PlatformOperatorActExecutor>(), NullLogger<ClosureFanOut>.Instance);
        }

        public IPlatformOperatorGate Gate { get; } = Substitute.For<IPlatformOperatorGate>();

        public StaticTenantContextAccessor Accessor { get; } = new(null);

        public ClosureFanOut FanOut { get; }

        public List<Act> Acts { get; } = [];

        /// <summary>The gate calls, counted from zero in the fan-out's order, that the gate refuses.</summary>
        public HashSet<int> RefuseAdmissionAt { get; } = [];

        public HashSet<ClosureParticipantGroup> Refuse { get; } = [];

        public HashSet<ClosureParticipantGroup> Throw { get; } = [];

        public Dictionary<ClosureParticipantGroup, ClosureGroupOutcome> Answer { get; } = [];

        public Action? OnAct { get; set; }

        public void Dispose() => _provider.Dispose();
    }
}

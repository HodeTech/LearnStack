using FluentAssertions;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// The closure owner's refusals before it reaches the database (ADR-0107 § 7): a deployment with no single fan-out
/// registered never enters the platform scope for a Begin, so it never moves a person to Closing, and an admission for
/// another act or a request that names nothing is refused by Begin and by Finalize before the admission is spent and
/// before the provider is asked anything.
/// </summary>
public sealed class AccountClosureOperatorOperationsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task A_Begin_without_exactly_one_fan_out_refuses_before_it_enters_the_scope(int registered)
    {
        var scope = Substitute.For<IPlatformAdminScope>();
        var owner = Owner(scope, [.. Enumerable.Range(0, registered).Select(_ => Substitute.For<IClosureFanOut>())]);
        var admission = TestPlatformAdmissions.Admission(PlatformOperations.ClosureBegin);

        var act = () => owner.BeginAsync(admission, Guid.CreateVersion7());

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("fan-out");
        await scope.DidNotReceiveWithAnyArgs().EnterAsync(default!, default, default, default, default);
        admission.TryConsume().Should().BeTrue("the refused act did not spend the admission");
    }

    [Fact]
    public async Task A_Begin_refuses_another_act_s_admission_and_a_request_that_names_nothing_before_anything_runs()
    {
        var scope = Substitute.For<IPlatformAdminScope>();
        var owner = Owner(scope, [Substitute.For<IClosureFanOut>()]);

        foreach (var operation in PlatformOperations.All.Where(operation => !ReferenceEquals(operation, PlatformOperations.ClosureBegin)))
        {
            var foreign = () => owner.BeginAsync(TestPlatformAdmissions.Admission(operation), Guid.CreateVersion7());
            (await foreign.Should().ThrowAsync<ArgumentException>(operation.AuditSlug)).Which.ParamName.Should().Be("admission");
        }

        var empty = () => owner.BeginAsync(TestPlatformAdmissions.Admission(PlatformOperations.ClosureBegin), Guid.Empty);
        (await empty.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("closureRequestId");
        await scope.DidNotReceiveWithAnyArgs().EnterAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task A_Finalize_refuses_another_act_s_admission_and_a_request_that_names_nothing_before_anything_runs()
    {
        var scope = Substitute.For<IPlatformAdminScope>();
        var removal = Substitute.For<IIdentityProviderAccountRemoval>();
        var owner = Owner(scope, [Substitute.For<IClosureFanOut>()], removal);

        foreach (var operation in PlatformOperations.All.Where(operation => !ReferenceEquals(operation, PlatformOperations.ClosureFinalize)))
        {
            var admission = TestPlatformAdmissions.Admission(operation);
            var foreign = () => owner.FinalizeAsync(admission, Guid.CreateVersion7());
            (await foreign.Should().ThrowAsync<ArgumentException>(operation.AuditSlug)).Which.ParamName.Should().Be("admission");
            admission.TryConsume().Should().BeTrue("the refused act did not spend the admission");
        }

        var empty = () => owner.FinalizeAsync(TestPlatformAdmissions.Admission(PlatformOperations.ClosureFinalize), Guid.Empty);
        (await empty.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("closureRequestId");
        await scope.DidNotReceiveWithAnyArgs().EnterAsync(default!, default, default, default, default);
        await removal.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default);
    }

    /// <summary>
    /// One bound governs every step of a closure: the participant groups Begin admits are what the fan-out takes in one call
    /// and what one lifecycle read answers, and the Memberships one tenant may hold are what one group's snapshot freezes and
    /// one redaction pass compares, which the plan factory itself refuses past.
    /// </summary>
    [Fact]
    public void One_bound_governs_the_closure_s_participant_read_fan_out_snapshot_and_redaction_pass()
    {
        ClosureLimits.MaximumParticipantGroups.Should().Be(256);
        ClosureFanOut.MaximumGroups.Should().Be(ClosureLimits.MaximumParticipantGroups);
        ClosureLimits.MaximumParticipantGroups.Should().BeLessThanOrEqualTo(IClosureGroupLifecycleReader.MaximumGroups,
            "one lifecycle read answers every participant group");
        ClosureLimits.MaximumTenantMemberships.Should().Be(DataRequestLimits.MaximumSnapshotMemberships);
        ClosureLimits.MaximumMemberships.Should().Be(ClosureLimits.MaximumParticipantGroups * ClosureLimits.MaximumTenantMemberships);

        ClosureLimits.Admits(ClosureLimits.MaximumParticipantGroups, ClosureLimits.MaximumTenantMemberships).Should().BeTrue();
        ClosureLimits.Admits(ClosureLimits.MaximumParticipantGroups + 1, 1).Should().BeFalse();
        ClosureLimits.Admits(1, ClosureLimits.MaximumTenantMemberships + 1).Should().BeFalse();

        var tenant = TenantId.From(Guid.CreateVersion7());
        var subject = LearnStack.SharedKernel.Identifiers.UserId.From(Guid.CreateVersion7());
        Guid[] Memberships(int count) => [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7())];
        LearnStack.SharedKernel.Audit.AuditRedactionPlan.ForClosureTenant(tenant, subject, Memberships(ClosureLimits.MaximumTenantMemberships))
            .MembershipIds.Should().HaveCount(ClosureLimits.MaximumTenantMemberships, "exactly the tenant bound is one pass");
        FluentActions.Invoking(() => LearnStack.SharedKernel.Audit.AuditRedactionPlan.ForClosureTenant(
                tenant, subject, Memberships(ClosureLimits.MaximumTenantMemberships + 1)))
            .Should().Throw<ArgumentException>("one Membership past it is no pass, which is why Begin refuses such a person");
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void A_tally_is_complete_only_when_no_served_group_is_waiting_or_failed(int waiting, int failed, bool complete) =>
        new ClosureBeginTally(Served: 3, Started: 3 - waiting - failed, Adopted: 0, Waiting: waiting, Failed: failed, Unserved: 1)
            .Complete.Should().Be(complete);

    private static AccountClosureOperatorOperations Owner(
        IPlatformAdminScope scope, IReadOnlyList<IClosureFanOut> fanOuts, IIdentityProviderAccountRemoval? removal = null) => new(
        scope, Substitute.For<IClosureGroupLifecycleReader>(), new NoHoldSource(), fanOuts,
        removal ?? Substitute.For<IIdentityProviderAccountRemoval>(), NullLogger<AccountClosureOperatorOperations>.Instance);
}

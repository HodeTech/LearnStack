using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// ADR-0107 § 5: the erasure domain operations accept the system actor and any status, are idempotent, run only under a
/// live witness minted from a fulfilling erasure of exactly the row's tenant, group and person, and leave the ordinary
/// methods' rules unchanged. A Revoked tombstone's own row is never stamped, because the database admits no UPDATE of it.
/// </summary>
public sealed class DataErasureDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.CreateVersion7());
    private static readonly UserId Subject = UserId.From(Guid.CreateVersion7());
    private static readonly UserId Administrator = UserId.From(Guid.CreateVersion7());

    [Theory]
    [InlineData(MembershipStatus.Active)]
    [InlineData(MembershipStatus.Suspended)]
    public void A_current_Membership_is_erased_to_a_Revoked_tombstone_by_the_system_actor_and_a_second_run_changes_nothing(
        MembershipStatus status)
    {
        var membership = Member(null);
        membership.SetProfile("Ada", "en", At, Administrator).IsSuccess.Should().BeTrue();
        if (status == MembershipStatus.Suspended)
        {
            membership.Suspend(At, Administrator).IsSuccess.Should().BeTrue();
        }

        using var source = new Source(Erasure(null, Subject));
        var witness = source.Mint();
        var later = At.AddDays(2);

        membership.RemoveAssignmentsForErasure(witness, later, UserId.SystemActor).IsSuccess.Should().BeTrue();
        membership.ClearProfileForErasure(witness, later, UserId.SystemActor).IsSuccess.Should().BeTrue();
        membership.RevokeForErasure(witness, later, UserId.SystemActor).IsSuccess.Should().BeTrue();

        membership.Status.Should().Be(MembershipStatus.Revoked);
        membership.UserId.Should().Be(Subject, "the tombstone keeps its pseudonymous person");
        membership.RoleAssignments.Should().BeEmpty();
        membership.Profile.DisplayName.Should().BeNull();
        membership.Profile.PreferredLocale.Should().BeNull();
        membership.Profile.CustomFieldsJson.Should().Be("[]");
        membership.DeletedBy.Should().Be(UserId.SystemActor);
        membership.UpdatedBy.Should().Be(UserId.SystemActor);
        membership.IsErased.Should().BeTrue();

        var version = membership.Version;
        membership.RemoveAssignmentsForErasure(witness, later.AddMinutes(1), UserId.SystemActor).IsSuccess.Should().BeTrue();
        membership.ClearProfileForErasure(witness, later.AddMinutes(1), UserId.SystemActor).IsSuccess.Should().BeTrue();
        membership.RevokeForErasure(witness, later.AddMinutes(1), UserId.SystemActor).IsSuccess.Should().BeTrue();
        membership.Version.Should().Be(version, "an erased Membership is left exactly as it is");
        membership.UpdatedAt.Should().Be(later);
    }

    /// <summary>
    /// An earlier-revoked Membership keeps its assignments and profile under the ordinary rules; the erasure clears them
    /// without touching the tombstone's own row, which the contextual guard admits no UPDATE of.
    /// </summary>
    [Fact]
    public void An_earlier_revoked_Membership_loses_its_assignments_and_profile_without_its_root_being_stamped()
    {
        var membership = Member(Branch);
        membership.SetProfile("Ada", "tr", At, Administrator).IsSuccess.Should().BeTrue();
        membership.Revoke(At.AddHours(1), Administrator).IsSuccess.Should().BeTrue();
        membership.SetProfile(null, null, At.AddHours(2), Administrator).IsFailure.Should().BeTrue("the ordinary method keeps its rule");
        var version = membership.Version;
        var stamped = (membership.UpdatedAt, membership.UpdatedBy, membership.DeletedAt, membership.DeletedBy);
        using var source = new Source(Erasure(Branch, Subject));

        membership.RemoveAssignmentsForErasure(source.Mint(), At.AddDays(2), UserId.SystemActor).IsSuccess.Should().BeTrue();
        membership.ClearProfileForErasure(source.Mint(), At.AddDays(2), UserId.SystemActor).IsSuccess.Should().BeTrue();
        membership.RevokeForErasure(source.Mint(), At.AddDays(2), UserId.SystemActor).IsSuccess.Should().BeTrue();

        membership.IsErased.Should().BeTrue();
        membership.Version.Should().Be(version);
        (membership.UpdatedAt, membership.UpdatedBy, membership.DeletedAt, membership.DeletedBy).Should().Be(stamped,
            "the tombstone records who revoked it; the erasure is on the audit row instead");
    }

    [Theory]
    [InlineData("no_witness")]
    [InlineData("closed_witness")]
    [InlineData("other_person")]
    [InlineData("other_group")]
    [InlineData("other_tenant")]
    [InlineData("request_ended")]
    public void Without_a_live_witness_for_exactly_the_rows_person_and_group_nothing_is_erased(string variant)
    {
        var membership = Member(null);
        var erasure = variant switch
        {
            "other_person" => Erasure(null, Administrator),
            "other_group" => Erasure(Branch, Subject),
            "other_tenant" => Erasure(null, Subject, TenantId.From(Guid.CreateVersion7())),
            _ => Erasure(null, Subject),
        };
        var source = new Source(erasure);
        var witness = variant == "no_witness" ? null : source.Mint();
        if (variant == "closed_witness")
        {
            source.Dispose();
        }

        if (variant == "request_ended")
        {
            erasure.AwaitPrivilegedStep(At.AddDays(2), UserId.SystemActor).IsSuccess.Should().BeTrue();
        }

        membership.RemoveAssignmentsForErasure(witness!, At.AddDays(2), UserId.SystemActor).IsFailure.Should().BeTrue();
        membership.ClearProfileForErasure(witness!, At.AddDays(2), UserId.SystemActor).IsFailure.Should().BeTrue();
        membership.RevokeForErasure(witness!, At.AddDays(2), UserId.SystemActor).IsFailure.Should().BeTrue();
        membership.Status.Should().Be(MembershipStatus.Active);
        membership.RoleAssignments.Should().ContainSingle();
        membership.Version.Should().Be(0);
        source.Dispose();
    }

    [Theory]
    [InlineData(DataRequestKind.Export)]
    [InlineData(DataRequestKind.Erasure)]
    public void A_witness_is_captured_only_from_a_fulfilling_erasure(DataRequestKind kind)
    {
        var request = DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), kind, Subject,
            new DataRequestScopeGroup(Tenant, null), [MembershipId.From(Guid.CreateVersion7())], At).Value!;
        using var source = new Source(request);

        FluentActions.Invoking(source.Mint).Should().Throw<InvalidOperationException>(kind == DataRequestKind.Export
            ? "an export never erases" : "a scheduled erasure has not reached its execution yet");
    }

    [Fact]
    public void A_pending_invitation_the_subject_issued_is_revoked_with_the_subject_as_its_actor_and_nothing_else_is()
    {
        using var source = new Source(Erasure(null, Subject));
        var witness = source.Mint();
        var issued = Invite(Subject);
        var later = At.AddDays(2);

        issued.RevokeForErasure(witness, later).IsSuccess.Should().BeTrue();

        issued.Status.Should().Be(InvitationStatus.Revoked);
        issued.UpdatedBy.Should().Be(Subject, "the aggregate refuses the system actor, so the subject is its actor");
        issued.VerifierDigest.Should().BeNull("a revoked offer can no longer be accepted");
        var version = issued.Version;
        issued.RevokeForErasure(witness, later.AddMinutes(1)).IsSuccess.Should().BeTrue();
        issued.Version.Should().Be(version, "an offer no longer pending changes nothing");

        var foreign = Invite(Administrator);
        foreign.RevokeForErasure(witness, later).IsFailure.Should().BeTrue("another person's offer is not the subject's to erase");
        foreign.Status.Should().Be(InvitationStatus.Pending);
        var branch = Invite(Subject, Branch);
        branch.RevokeForErasure(witness, later).IsFailure.Should().BeTrue("another group's offer is outside the request");
        branch.Status.Should().Be(InvitationStatus.Pending);
    }

    private static DataSubjectRequest Erasure(OrganizationId? organization, UserId subject, TenantId? tenant = null)
    {
        var request = DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Erasure, subject,
            new DataRequestScopeGroup(tenant ?? Tenant, organization), [MembershipId.From(Guid.CreateVersion7())], At).Value!;
        request.StartFulfilling(At + DataRequestLimits.CancellationWindow, UserId.SystemActor).IsSuccess.Should().BeTrue();
        return request;
    }

    /// <summary>An Active Membership of the subject with one role; a test moves it on itself.</summary>
    private static Membership Member(OrganizationId? organization)
    {
        var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, BuiltInRoleKey.Learner,
            organization is null ? PermissionScope.Tenant : PermissionScope.Organization, At, Administrator).Value!;
        return Membership.Create(MembershipId.From(Guid.CreateVersion7()), Tenant, organization, Subject, role, At,
            Administrator).Value!;
    }

    private static Invitation Invite(UserId inviter, OrganizationId? organization = null)
    {
        var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, BuiltInRoleKey.Learner,
            organization is null ? PermissionScope.Tenant : PermissionScope.Organization, At, inviter).Value!;
        return Invitation.Create(InvitationId.From(Guid.CreateVersion7()), Tenant, organization, "recipient@example.invalid", role,
            new InvitationDelivery(InvitationGenerationId.From(Guid.CreateVersion7()), BackgroundJobId.From(Guid.CreateVersion7()),
                new string('a', 64), "https://manage.school.invalid", "en", 14), At, inviter).Value!;
    }

    /// <summary>A test minter over one request: live until disposed, like the production source.</summary>
    private sealed class Source(DataSubjectRequest request) : DataErasureWitness.MintingSource, IDisposable
    {
        private bool _closed;

        protected override bool IsEvidenceCurrent => !_closed && request.Status == DataRequestStatus.Fulfilling;

        public DataErasureWitness Mint() => Capture(request);

        public void Dispose() => _closed = true;
    }
}

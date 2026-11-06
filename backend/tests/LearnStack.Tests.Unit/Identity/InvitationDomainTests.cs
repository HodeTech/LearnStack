using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using RoleEntity = LearnStack.Modules.Identity.Domain.Role;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class InvitationDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.CreateVersion7());
    private static readonly UserId Inviter = UserId.From(Guid.CreateVersion7());
    private static readonly UserId Invitee = UserId.From(Guid.CreateVersion7());

    [Theory]
    [InlineData("Merkez Sube", "Merkez Sube")]
    [InlineData("Merkez\nSube", "Merkez Sube")]
    [InlineData("  Merkez\r\n\t Sube \u0085 ", "Merkez Sube")]
    [InlineData("\u0001A\u0007\u0007B\u007f", "A B")]
    public void Display_label_shows_one_clean_line_for_a_name_holding_control_characters(string input, string expected)
    {
        InvitationDisplayLabel.TryNormalize(input, out var actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\u0001\u0002")]
    [InlineData("\n \t")]
    public void Display_label_refuses_an_empty_or_control_only_name(string? input)
    {
        InvitationDisplayLabel.TryNormalize(input, out var actual).Should().BeFalse();
        actual.Should().BeEmpty();
    }

    [Fact]
    public void Display_label_keeps_the_stored_length_bound()
    {
        InvitationDisplayLabel.TryNormalize(new string('a', InvitationDisplayLabel.MaximumLength), out _).Should().BeTrue();
        InvitationDisplayLabel.TryNormalize(new string('a', InvitationDisplayLabel.MaximumLength + 1), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Person+tag@School.Example", "person+tag@school.example")]
    [InlineData("O'Name@EXAMPLE.COM", "o'name@example.com")]
    [InlineData("a.b-c_d@learn-stack.local", "a.b-c_d@learn-stack.local")]
    public void Mailbox_normalizes_ASCII_without_alias_rewriting(string input, string expected)
    {
        InvitationMailbox.TryNormalize(input, out var actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" a@example.com")]
    [InlineData("a@example.com\r\nBcc:other@example.com")]
    [InlineData(".a@example.com")]
    [InlineData("a..b@example.com")]
    [InlineData("a.@example.com")]
    [InlineData("a@@example.com")]
    [InlineData("a@-example.com")]
    [InlineData("a@example..com")]
    [InlineData("a@éxample.com")]
    [InlineData("名@example.com")]
    [InlineData("\"a\"@example.com")]
    public void Invalid_mailbox_has_no_normalized_or_diagnostic_copy(string? input)
    {
        InvitationMailbox.TryNormalize(input, out var actual).Should().BeFalse();
        actual.Should().BeEmpty();
    }

    [Fact]
    public void Mailbox_length_boundary_is_in_ASCII_bytes()
    {
        var email = new string('a', 64) + "@" + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 61);
        email.Length.Should().Be(254);
        InvitationMailbox.TryNormalize(email, out _).Should().BeTrue();
        InvitationMailbox.TryNormalize("a" + email, out _).Should().BeFalse();
    }

    [Fact]
    public void Creation_freezes_scope_role_and_first_queued_generation()
    {
        var role = Role();
        var delivery = Delivery();
        var invitation = Create(role, delivery);
        invitation.RecipientEmail.Should().Be("recipient@example.invalid");
        invitation.InviterUserId.Should().Be(Inviter);
        invitation.RoleId.Should().Be(role.Id);
        invitation.PinnedRoleVersion.Should().Be(role.Version);
        invitation.Generation.Should().Be(1);
        invitation.GenerationId.Should().Be(delivery.GenerationId);
        invitation.CurrentDeliveryJobId.Should().Be(delivery.JobId);
        invitation.ExpiresAt.Should().Be(At.AddDays(14));
        invitation.SubmissionStatus.Should().Be(InvitationSubmissionStatus.Queued);
        invitation.Status.Should().Be(InvitationStatus.Pending);
        invitation.Version.Should().Be(0);
        invitation.ToString().Should().NotContain(invitation.RecipientEmail);
        delivery.ToString().Should().NotContain(delivery.VerifierDigest);
    }

    [Fact]
    public void Role_scope_is_compatible_with_the_exact_target_not_a_delegation_bridge()
    {
        Invitation.Create(InvitationId.From(Guid.CreateVersion7()), Tenant, Organization,
            "recipient@example.invalid", Role(), Delivery(), At, Inviter).IsFailure.Should().BeTrue();
        var organizationRole = Role(PermissionScope.Organization);
        Invitation.Create(InvitationId.From(Guid.CreateVersion7()), Tenant, null,
            "recipient@example.invalid", organizationRole, Delivery(), At, Inviter).IsFailure.Should().BeTrue();
        Invitation.Create(InvitationId.From(Guid.CreateVersion7()), Tenant, Organization,
            "recipient@example.invalid", organizationRole, Delivery(), At, Inviter).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://school.example")]
    [InlineData("https://school.example/path")]
    [InlineData("https://school.example?returnTo=other")]
    [InlineData("https://school.example#code")]
    [InlineData("https://person@school.example")]
    public void Delivery_rejects_nonorigin_and_unencrypted_addresses(string origin)
    {
        Invitation.Create(InvitationId.From(Guid.CreateVersion7()), Tenant, null,
            "recipient@example.invalid", Role(), Delivery() with { EntryOrigin = origin }, At, Inviter)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Expired_reissue_preserves_offer_pin_and_invalidates_the_previous_verifier()
    {
        var role = Role();
        var invitation = Create(role);
        var previous = invitation.VerifierDigest!;
        var delivery = Delivery('b') with { LifetimeDays = 1 };
        invitation.Reissue(role, delivery, At.AddDays(20), Inviter).IsSuccess.Should().BeTrue();
        invitation.Generation.Should().Be(2);
        invitation.Version.Should().Be(1);
        invitation.ExpiresAt.Should().Be(At.AddDays(21));
        invitation.InviterUserId.Should().Be(Inviter);
        invitation.MatchesPendingVerifier(previous).Should().BeFalse();
        invitation.MatchesPendingVerifier(delivery.VerifierDigest).Should().BeTrue();
        invitation.ResultMembershipId.Should().BeNull();
    }

    [Fact]
    public void Changed_role_cannot_be_silently_repinned_by_reissue()
    {
        var role = Role();
        var invitation = Create(role);
        role.ReplaceGrants([], At.AddMinutes(1), Inviter).IsSuccess.Should().BeTrue();
        invitation.Reissue(role, Delivery('b'), At.AddMinutes(2), Inviter).IsFailure.Should().BeTrue();
        invitation.Version.Should().Be(0);
        invitation.Generation.Should().Be(1);
    }

    [Fact]
    public void Reissue_requires_a_new_verifier_in_addition_to_new_identifiers()
    {
        var role = Role();
        var invitation = Create(role);
        invitation.Reissue(role, Delivery(), At.AddMinutes(1), Inviter).IsFailure.Should().BeTrue();
        invitation.Version.Should().Be(0);
        invitation.Generation.Should().Be(1);
    }

    [Fact]
    public void Revocation_is_terminal_and_not_soft_deletion()
    {
        var role = Role();
        var invitation = Create(role);
        var digest = invitation.VerifierDigest!;
        invitation.Revoke(At.AddMinutes(1), Inviter).IsSuccess.Should().BeTrue();
        invitation.Status.Should().Be(InvitationStatus.Revoked);
        invitation.IsDeleted.Should().BeFalse();
        invitation.VerifierDigest.Should().BeNull();
        invitation.ConsumedVerifierDigest.Should().BeNull();
        invitation.MatchesPendingVerifier(digest).Should().BeFalse();
        invitation.Reissue(role, Delivery(), At.AddMinutes(2), Inviter).IsFailure.Should().BeTrue();
        invitation.Revoke(At.AddMinutes(2), Inviter).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Acceptance_consumes_once_and_retains_exact_original_result()
    {
        var role = Role();
        var invitation = Create(role);
        var member = Member(role);
        var digest = invitation.VerifierDigest!;
        invitation.Accept(member, InvitationAcceptanceDisposition.Created, digest, At.AddMinutes(1), Invitee)
            .IsSuccess.Should().BeTrue();
        invitation.ResultMembershipId.Should().Be(member.Id);
        invitation.AcceptedBy.Should().Be(Invitee);
        invitation.AcceptanceDisposition.Should().Be(InvitationAcceptanceDisposition.Created);
        invitation.VerifierDigest.Should().BeNull();
        invitation.MatchesConsumedVerifier(digest).Should().BeTrue();
        invitation.Accept(member, InvitationAcceptanceDisposition.Created, digest, At.AddMinutes(2), Invitee)
            .IsFailure.Should().BeTrue();
        invitation.Reissue(role, Delivery(), At.AddMinutes(2), Inviter).IsFailure.Should().BeTrue();
        invitation.Revoke(At.AddMinutes(2), Inviter).IsFailure.Should().BeTrue();
        invitation.Version.Should().Be(1);
    }

    [Fact]
    public void Existing_active_member_keeps_all_roles_profile_and_version()
    {
        var offered = Role();
        var existingRole = RoleEntity.Create(RoleId.From(Guid.CreateVersion7()), Tenant,
            BuiltInRoleKey.Instructor, PermissionScope.Tenant, At, Inviter).Value!;
        var member = Member(existingRole);
        member.SetProfile("Existing name", "en", At.AddSeconds(1), Invitee).IsSuccess.Should().BeTrue();
        var version = member.Version;
        var invitation = Create(offered);
        invitation.Accept(member, InvitationAcceptanceDisposition.AlreadyMember, invitation.VerifierDigest!,
            At.AddMinutes(1), Invitee).IsSuccess.Should().BeTrue();
        member.Version.Should().Be(version);
        member.Profile.DisplayName.Should().Be("Existing name");
        member.RoleAssignments.Should().ContainSingle().Which.RoleId.Should().Be(existingRole.Id);
        invitation.AcceptanceDisposition.Should().Be(InvitationAcceptanceDisposition.AlreadyMember);
    }

    [Fact]
    public void Suspended_member_and_exact_expiry_refuse_without_consumption()
    {
        var role = Role();
        var member = Member(role);
        var invitation = Create(role);
        member.Suspend(At.AddSeconds(1), Inviter).IsSuccess.Should().BeTrue();
        invitation.Accept(member, InvitationAcceptanceDisposition.AlreadyMember, invitation.VerifierDigest!,
            At.AddMinutes(1), Invitee).IsFailure.Should().BeTrue();
        member.Reactivate(At.AddSeconds(2), Inviter).IsSuccess.Should().BeTrue();
        invitation.Accept(member, InvitationAcceptanceDisposition.Created, invitation.VerifierDigest!,
            invitation.ExpiresAt, Invitee).IsFailure.Should().BeTrue();
        invitation.Status.Should().Be(InvitationStatus.Pending);
        invitation.Version.Should().Be(0);
        invitation.Accept(member, InvitationAcceptanceDisposition.Created, invitation.VerifierDigest!,
            invitation.ExpiresAt.AddTicks(-1), Invitee).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Wrong_actor_scope_or_verifier_cannot_supply_an_acceptance_result()
    {
        var role = Role();
        var invitation = Create(role);
        var member = Member(role);
        invitation.Accept(member, InvitationAcceptanceDisposition.Created, new string('b', 64), At.AddMinutes(1), Invitee)
            .IsFailure.Should().BeTrue();
        invitation.Accept(member, InvitationAcceptanceDisposition.Created, invitation.VerifierDigest!, At.AddMinutes(1), Inviter)
            .IsFailure.Should().BeTrue();
        invitation.Version.Should().Be(0);
        invitation.ConsumedVerifierDigest.Should().BeNull();
    }

    [Fact]
    public void Delivery_finalization_is_write_once_and_stale_generation_does_not_mutate()
    {
        var invitation = Create(Role());
        var receipt = InvitationEmailReceiptId.From(Guid.CreateVersion7());
        invitation.RecordSubmission(InvitationGenerationId.From(Guid.CreateVersion7()), invitation.CurrentDeliveryJobId,
            receipt, InvitationSubmissionStatus.SubmissionAccepted, At.AddMinutes(1), Inviter).IsSuccess.Should().BeTrue();
        invitation.Version.Should().Be(0);
        invitation.RecordSubmission(invitation.GenerationId, invitation.CurrentDeliveryJobId, receipt,
            InvitationSubmissionStatus.SubmissionAccepted, At.AddMinutes(1), Inviter).IsSuccess.Should().BeTrue();
        invitation.RecordSubmission(invitation.GenerationId, invitation.CurrentDeliveryJobId, receipt,
            InvitationSubmissionStatus.SubmissionAccepted, At.AddMinutes(2), Inviter).IsSuccess.Should().BeTrue();
        invitation.RecordSubmission(invitation.GenerationId, invitation.CurrentDeliveryJobId, InvitationEmailReceiptId.From(Guid.CreateVersion7()),
            InvitationSubmissionStatus.Failed, At.AddMinutes(2), Inviter).IsFailure.Should().BeTrue();
        invitation.Version.Should().Be(1);
    }

    [Fact]
    public void Known_workflow_failure_is_receiptless_write_once_and_cannot_overwrite_submission()
    {
        var role = Role();
        var invitation = Create(role);
        invitation.RecordWorkflowFailure(invitation.GenerationId, invitation.CurrentDeliveryJobId, At.AddMinutes(1), Inviter)
            .IsSuccess.Should().BeTrue();
        invitation.SubmissionStatus.Should().Be(InvitationSubmissionStatus.Failed);
        invitation.SubmissionReceiptId.Should().BeNull();
        invitation.RecordWorkflowFailure(invitation.GenerationId, invitation.CurrentDeliveryJobId, At.AddMinutes(2), Inviter)
            .IsSuccess.Should().BeTrue();
        invitation.Version.Should().Be(1);
        invitation.RecordSubmission(invitation.GenerationId, invitation.CurrentDeliveryJobId, InvitationEmailReceiptId.From(Guid.CreateVersion7()),
            InvitationSubmissionStatus.SubmissionAccepted, At.AddMinutes(2), Inviter).IsFailure.Should().BeTrue();
        invitation.Reissue(role, Delivery('b'), At.AddMinutes(3), Inviter).IsSuccess.Should().BeTrue();
        invitation.SubmissionStatus.Should().Be(InvitationSubmissionStatus.Queued);
    }

    private static Invitation Create(Role role, InvitationDelivery? delivery = null) => Invitation.Create(
        InvitationId.From(Guid.CreateVersion7()), Tenant, null, "Recipient@Example.Invalid", role, delivery ?? Delivery(), At, Inviter).Value!;

    private static Role Role(PermissionScope scope = PermissionScope.Tenant) => RoleEntity.Create(
        RoleId.From(Guid.CreateVersion7()), Tenant, BuiltInRoleKey.Learner, scope, At, Inviter).Value!;

    private static Membership Member(Role role) => Membership.Create(MembershipId.From(Guid.CreateVersion7()),
        Tenant, null, Invitee, role, At, Inviter).Value!;

    private static InvitationDelivery Delivery(char digest = 'a') => new(InvitationGenerationId.From(Guid.CreateVersion7()),
        BackgroundJobId.From(Guid.CreateVersion7()), new string(digest, 64), "https://manage.school.invalid:3000", "en", 14);
}

using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations", table =>
        {
            table.HasCheckConstraint("ck_invitations_identifiers", "uuid_extract_version(id)=7 AND uuid_extract_version(generation_id)=7 AND uuid_extract_version(current_delivery_job_id)=7");
            table.HasCheckConstraint("ck_invitations_tenant", "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
            table.HasCheckConstraint("ck_invitations_generation", "generation > 0 AND pinned_role_version >= 0");
            table.HasCheckConstraint("ck_invitations_times", "isfinite(created_at) AND isfinite(generation_created_at) AND isfinite(expires_at) AND generation_created_at >= created_at AND expires_at >= generation_created_at + interval '1 day' AND expires_at <= generation_created_at + interval '30 days'");
            table.HasCheckConstraint("ck_invitations_no_delete", "deleted_at IS NULL AND deleted_by IS NULL");
            table.HasCheckConstraint("ck_invitations_mailbox", "octet_length(recipient_email) BETWEEN 3 AND 254 AND recipient_email=lower(recipient_email COLLATE \"C\") AND recipient_email ~ '^[!-~]+@[!-~]+$'");
            table.HasCheckConstraint("ck_invitations_lifecycle", """
                ((status='Pending' AND octet_length(verifier_digest)=32 AND consumed_verifier_digest IS NULL
                    AND accepted_by IS NULL AND result_membership_id IS NULL AND acceptance_disposition IS NULL AND accepted_at IS NULL)
                OR (status='Accepted' AND verifier_digest IS NULL AND octet_length(consumed_verifier_digest)=32
                    AND accepted_by IS NOT NULL AND result_membership_id IS NOT NULL
                    AND acceptance_disposition IN ('Created','AlreadyMember') AND accepted_at IS NOT NULL
                    AND isfinite(accepted_at) AND accepted_at >= generation_created_at AND accepted_at < expires_at)
                OR (status='Revoked' AND verifier_digest IS NULL AND consumed_verifier_digest IS NULL
                    AND accepted_by IS NULL AND result_membership_id IS NULL AND acceptance_disposition IS NULL AND accepted_at IS NULL)) IS TRUE
                """);
            table.HasCheckConstraint("ck_invitations_submission", "((submission_status='Queued' AND submission_receipt_id IS NULL) OR (submission_status IN ('SubmissionAccepted','Failed') AND submission_receipt_id IS NOT NULL AND submission_receipt_id <> '00000000-0000-0000-0000-000000000000'::uuid)) IS TRUE");
        });
        builder.HasKey(entity => entity.Id).HasName("pk_invitations");
        builder.Property(entity => entity.Id).HasConversion<InvitationId.EfCoreValueConverter, InvitationId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Scope("invitations");
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id }).HasName("ux_invitations_tenant_id_id");
        builder.Property(entity => entity.RecipientEmail).HasMaxLength(254).UseCollation("C");
        builder.Property(entity => entity.InviterUserId).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(entity => entity.RoleId).HasConversion<RoleId.EfCoreValueConverter, RoleId.EfCoreValueComparer>();
        builder.Property(entity => entity.GenerationId).HasConversion<InvitationGenerationId.EfCoreValueConverter, InvitationGenerationId.EfCoreValueComparer>();
        builder.Property(entity => entity.CurrentDeliveryJobId).HasConversion<BackgroundJobId.EfCoreValueConverter, BackgroundJobId.EfCoreValueComparer>();
        builder.Property(entity => entity.Status).HasEnumAsText();
        builder.Property(entity => entity.SubmissionStatus).HasEnumAsText();
        builder.Property(entity => entity.SubmissionReceiptId).HasConversion<InvitationEmailReceiptId.EfCoreValueConverter, InvitationEmailReceiptId.EfCoreValueComparer>();
        builder.Property(entity => entity.AcceptanceDisposition).HasConversion<string>();
        builder.Property(entity => entity.AcceptedBy).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(entity => entity.ResultMembershipId).HasConversion<MembershipId.EfCoreValueConverter, MembershipId.EfCoreValueComparer>();
        builder.Property(entity => entity.VerifierDigest).HasConversion(
            value => value == null ? null : Convert.FromHexString(value),
            value => value == null ? null : Convert.ToHexStringLower(value)).HasColumnType("bytea");
        builder.Property(entity => entity.ConsumedVerifierDigest).HasConversion(
            value => value == null ? null : Convert.FromHexString(value),
            value => value == null ? null : Convert.ToHexStringLower(value)).HasColumnType("bytea");
        builder.Property(entity => entity.EntryOrigin).HasMaxLength(512);
        builder.Property(entity => entity.EntryLocale).HasMaxLength(35);
        builder.HasOne<User>().WithMany().HasForeignKey(entity => entity.InviterUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_invitations_inviter");
        builder.HasIndex(entity => entity.InviterUserId).HasDatabaseName("ix_invitations_inviter_user_id");
        builder.HasOne<User>().WithMany().HasForeignKey(entity => entity.AcceptedBy)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_invitations_acceptor");
        builder.HasIndex(entity => entity.AcceptedBy).HasDatabaseName("ix_invitations_accepted_by");
        builder.HasOne<Role>().WithMany().HasForeignKey(entity => new { entity.TenantId, entity.RoleId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_invitations_role");
        builder.HasIndex(entity => new { entity.TenantId, entity.RoleId }).HasDatabaseName("ix_invitations_tenant_id_role_id");
        builder.HasOne<Membership>().WithMany().HasForeignKey(entity => new { entity.TenantId, entity.ResultMembershipId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_invitations_result_membership");
        builder.HasIndex(entity => new { entity.TenantId, entity.ResultMembershipId }).HasDatabaseName("ix_invitations_tenant_id_result_membership_id");
        builder.HasIndex(entity => new { entity.TenantId, entity.OrganizationId, entity.RecipientEmail })
            .IsUnique().AreNullsDistinct(false).HasFilter("status = 'Pending' AND deleted_at IS NULL").HasDatabaseName("ux_invitations_pending_recipient");
        builder.MapAuditColumns<Invitation, InvitationId>();
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "Scoped membership offer, recipient, credential verifiers, inviter and immutable acceptance/delivery linkage.",
            nameof(Invitation.Id), nameof(Invitation.TenantId), nameof(Invitation.OrganizationId), nameof(Invitation.RecipientEmail),
            nameof(Invitation.InviterUserId), nameof(Invitation.RoleId), nameof(Invitation.PinnedRoleVersion), nameof(Invitation.Status),
            nameof(Invitation.GenerationId), nameof(Invitation.Generation), nameof(Invitation.GenerationCreatedAt), nameof(Invitation.ExpiresAt),
            nameof(Invitation.VerifierDigest), nameof(Invitation.ConsumedVerifierDigest), nameof(Invitation.EntryOrigin), nameof(Invitation.EntryLocale),
            nameof(Invitation.CurrentDeliveryJobId), nameof(Invitation.SubmissionStatus), nameof(Invitation.SubmissionReceiptId),
            nameof(Invitation.AcceptedBy), nameof(Invitation.ResultMembershipId), nameof(Invitation.AcceptanceDisposition), nameof(Invitation.AcceptedAt));
    }
}

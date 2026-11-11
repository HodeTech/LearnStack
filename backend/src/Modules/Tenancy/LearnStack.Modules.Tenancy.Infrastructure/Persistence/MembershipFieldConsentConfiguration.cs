using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

internal sealed class MembershipFieldConsentConfiguration : IEntityTypeConfiguration<MembershipFieldConsent>
{
    public void Configure(EntityTypeBuilder<MembershipFieldConsent> builder)
    {
        builder.ToTable("membership_field_consents", table =>
        {
            table.HasCheckConstraint("ck_membership_field_consents_state", "state IN ('Granted','Withdrawn')");
            table.HasCheckConstraint("ck_membership_field_consents_sequence", "sequence BETWEEN 1 AND 9007199254740991 AND predecessor_sequence = sequence - 1 AND ((predecessor_sequence = 0 AND predecessor_id IS NULL) OR (predecessor_sequence > 0 AND predecessor_id IS NOT NULL))");
            table.HasCheckConstraint("ck_membership_field_consents_immutable_audit", "updated_at IS NULL AND updated_by IS NULL AND deleted_at IS NULL AND deleted_by IS NULL AND row_version = 0");
            table.HasCheckConstraint("ck_membership_field_consents_actor", "actor_user_id = subject_user_id AND created_by = actor_user_id");
            table.HasCheckConstraint("ck_membership_field_consents_pins", "definition_version > 0 AND length(purpose_key) BETWEEN 1 AND 64 AND purpose_key ~ '^[a-z0-9-]+$' AND length(notice_locale) BETWEEN 1 AND 35 AND notice_hash ~ '^[0-9a-f]{64}$'");
        });
        builder.HasKey(x => x.Id).HasName("pk_membership_field_consents");
        builder.Property(x => x.Id).HasConversion<MembershipFieldConsentId.EfCoreValueConverter, MembershipFieldConsentId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Property(x => x.PredecessorId).HasConversion<MembershipFieldConsentId.EfCoreValueConverter, MembershipFieldConsentId.EfCoreValueComparer>();
        builder.Property(x => x.TenantId).HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>();
        builder.Property(x => x.OrganizationId).HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>();
        builder.Property(x => x.SubjectUserId).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(x => x.ActorUserId).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(x => x.PurposeKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.NoticeLocale).HasMaxLength(35).IsRequired();
        builder.Property(x => x.NoticeHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.State).HasEnumAsText();
        builder.HasIndex(x => new
        {
            x.TenantId,
            x.OrganizationId,
            x.MembershipId,
            x.SubjectUserId,
            x.DefinitionId,
            x.DefinitionVersion,
            x.PurposeKey,
            x.Sequence
        })
            .IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_membership_field_consents_tuple_sequence");
        builder.HasIndex(x => new { x.SubjectUserId, x.MembershipId }).HasDatabaseName("ix_membership_field_consents_subject_membership");
        builder.HasIndex(x => new { x.TenantId, x.OrganizationId }).HasDatabaseName("ix_membership_field_consents_tenant_organization");
        builder.MapAuditColumns<MembershipFieldConsent, MembershipFieldConsentId>();
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("clock_timestamp()").ValueGeneratedOnAdd();
        builder.DeclarePersonalData(PersonalDataCategory.Identity, "Consent subject and actor linkage.",
            nameof(MembershipFieldConsent.SubjectUserId), nameof(MembershipFieldConsent.ActorUserId));
        builder.DeclarePersonalData(PersonalDataCategory.Sensitive, "Exact field and displayed notice linkage.",
            nameof(MembershipFieldConsent.MembershipId), nameof(MembershipFieldConsent.DefinitionId),
            nameof(MembershipFieldConsent.DefinitionVersion), nameof(MembershipFieldConsent.PurposeKey),
            nameof(MembershipFieldConsent.NoticeLocale), nameof(MembershipFieldConsent.NoticeHash));
        builder.DeclarePersonalData(PersonalDataCategory.Audit, "Immutable consent transition and predecessor.",
            nameof(MembershipFieldConsent.Sequence), nameof(MembershipFieldConsent.State),
            nameof(MembershipFieldConsent.PredecessorId), nameof(MembershipFieldConsent.PredecessorSequence));
        builder.DeclarePersonalData(PersonalDataCategory.None, "Consent record and isolation scope identifiers.",
            nameof(MembershipFieldConsent.Id), nameof(MembershipFieldConsent.TenantId), nameof(MembershipFieldConsent.OrganizationId));
    }
}

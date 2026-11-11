using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

internal sealed class ProviderEventTargetConfiguration : IEntityTypeConfiguration<ProviderEventTarget>
{
    public void Configure(EntityTypeBuilder<ProviderEventTarget> builder)
    {
        builder.ToTable("identity_provider_event_targets", table =>
        {
            table.HasCheckConstraint("ck_identity_provider_event_targets_ids", "(uuid_extract_version(id)=7 AND uuid_extract_version(event_id)=7 AND uuid_extract_version(receipt_id)=7) IS TRUE");
            table.HasCheckConstraint("ck_identity_provider_event_targets_tenant", "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
            table.HasCheckConstraint("ck_identity_provider_event_targets_subject", "octet_length(subject_key)=32 AND affected_user_id <> '00000000-0000-7000-8000-000000000001'::uuid");
            table.HasCheckConstraint("ck_identity_provider_event_targets_claim", "(claim_token IS NULL)=(claim_until IS NULL) AND attempts >= 0");
            table.HasCheckConstraint("ck_identity_provider_event_targets_time", "isfinite(received_at) AND isfinite(source_occurred_at) AND (claim_until IS NULL OR isfinite(claim_until)) AND (enqueued_at IS NULL OR isfinite(enqueued_at))");
        });
        builder.HasKey(row => row.Id).HasName("pk_identity_provider_event_targets");
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.TenantId).HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>();
        builder.Property(row => row.AuthorityId).HasConversion<IdentityAuthorityId.EfCoreValueConverter, IdentityAuthorityId.EfCoreValueComparer>();
        builder.Property(row => row.AffectedUserId).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(row => row.RealmId).HasMaxLength(255).IsRequired();
        builder.Property(row => row.SourceKind).HasMaxLength(80).IsRequired();
        builder.Property(row => row.SubjectKey).HasColumnType("bytea").IsRequired();
        builder.HasIndex(row => row.EventId).IsUnique().HasDatabaseName("ux_identity_provider_event_targets_event");
        builder.HasIndex(row => new { row.AuthorityId, row.RealmId, row.ProviderEventId, row.TenantId })
            .IsUnique().HasDatabaseName("ux_identity_provider_event_targets_source_tenant");
        builder.HasIndex(row => new { row.TenantId, row.Id }).HasDatabaseName("ix_identity_provider_event_targets_tenant_id");
        builder.HasIndex(row => new { row.AffectedUserId, row.Id }).HasDatabaseName("ix_identity_provider_event_targets_subject_id");
        builder.HasIndex(row => new { row.ReceivedAt, row.Id }).HasFilter("enqueued_at IS NULL")
            .HasDatabaseName("ix_identity_provider_event_targets_pending");
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "Minimized provider-history target and stable delivery linkage; retained until coordinated transport/Audit retirement.",
            nameof(ProviderEventTarget.Id), nameof(ProviderEventTarget.TenantId), nameof(ProviderEventTarget.ReceiptId),
            nameof(ProviderEventTarget.EventId), nameof(ProviderEventTarget.AuthorityId), nameof(ProviderEventTarget.RealmId),
            nameof(ProviderEventTarget.ProviderEventId), nameof(ProviderEventTarget.AffectedUserId), nameof(ProviderEventTarget.SubjectKey),
            nameof(ProviderEventTarget.SourceOccurredAt), nameof(ProviderEventTarget.SourceKind), nameof(ProviderEventTarget.ReceivedAt));
        builder.DeclarePersonalData(PersonalDataCategory.None, "Internal lease and enqueue progress; no subject content.",
            nameof(ProviderEventTarget.ClaimToken), nameof(ProviderEventTarget.ClaimUntil), nameof(ProviderEventTarget.Attempts),
            nameof(ProviderEventTarget.EnqueuedAt));
    }
}

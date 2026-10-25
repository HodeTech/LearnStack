using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_actor", "uuid_extract_version(id) = 7 AND id <> '00000000-0000-7000-8000-000000000001'::uuid");
            table.HasCheckConstraint("ck_users_subject_key", "octet_length(subject_key) = 32");
            table.HasCheckConstraint("ck_users_subject_digest", "provider_subject IS NULL OR subject_key = sha256(int4send(octet_length(convert_to('learnstack.identity.subject.v1','UTF8'))) || convert_to('learnstack.identity.subject.v1','UTF8') || int4send(16) || uuid_send(authority_id) || int4send(octet_length(convert_to(provider_subject,'UTF8'))) || convert_to(provider_subject,'UTF8'))");
            table.HasCheckConstraint("ck_users_status", "status IN ('Active', 'Closing', 'Closed')");
            table.HasCheckConstraint("ck_users_subject_lifecycle", "(status = 'Active' AND provider_subject IS NOT NULL AND length(provider_subject) BETWEEN 1 AND 255) OR (status = 'Closing' AND (provider_subject IS NULL OR length(provider_subject) BETWEEN 1 AND 255)) OR (status = 'Closed' AND provider_subject IS NULL)");
            table.HasCheckConstraint("ck_users_no_soft_delete", "deleted_at IS NULL AND deleted_by IS NULL");
        });
        builder.HasKey(user => user.Id).HasName("pk_users");
        builder.Property(user => user.Id)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Property(user => user.AuthorityId)
            .HasConversion<IdentityAuthorityId.EfCoreValueConverter, IdentityAuthorityId.EfCoreValueComparer>();
        builder.Property(user => user.SubjectKey).HasConversion(
            new ValueConverter<IdentitySubjectKey, byte[]>(value => value.ToByteArray(), value => IdentitySubjectKey.FromBytes(value)),
            new ValueComparer<IdentitySubjectKey>((left, right) => left != null && left.Equals(right),
                value => value.GetHashCode(), value => value)).HasColumnType("bytea").IsRequired();
        builder.Property(user => user.ProviderSubject).HasMaxLength(255);
        builder.Property(user => user.Status).HasEnumAsText().IsRequired();
        builder.Property(user => user.CreationAuditId)
            .HasConversion<AuditEntryId.EfCoreValueConverter, AuditEntryId.EfCoreValueComparer>();
        builder.Property(user => user.CreationAuditTimestamp).IsRequired();
        builder.HasIndex(user => new { user.AuthorityId, user.SubjectKey })
            .IsUnique().HasDatabaseName("ux_users_authority_id_subject_key");
        builder.HasOne<IdentityAuthority>().WithMany().HasForeignKey(user => user.AuthorityId)
            .HasPrincipalKey(authority => authority.AuthorityId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_users_identity_authority");
        builder.MapAuditColumns<User, UserId>();
        // The app's INSERT grant covers initial creation only. EF otherwise
        // explicitly inserts the four NULL update/deletion fields as well.
        foreach (var name in new[] { nameof(User.UpdatedAt), nameof(User.UpdatedBy),
                     nameof(User.DeletedAt), nameof(User.DeletedBy) })
            builder.Metadata.FindProperty(name)!.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "Permanent actor/provider binding and creation-audit linkage; retained to prevent recreation after closure.",
            nameof(User.Id), nameof(User.AuthorityId), nameof(User.SubjectKey), nameof(User.ProviderSubject),
            nameof(User.CreationAuditId), nameof(User.CreationAuditTimestamp));
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "Admission lifecycle of the bound person; no tenant membership or profile is implied.", nameof(User.Status));
    }
}

internal sealed class IdentityAuthorityConfiguration : IEntityTypeConfiguration<IdentityAuthority>
{
    public void Configure(EntityTypeBuilder<IdentityAuthority> builder)
    {
        builder.ToTable("identity_authority", table =>
        {
            table.HasCheckConstraint("ck_identity_authority_singleton", "id = 1");
            table.HasCheckConstraint("ck_identity_authority_assigned", "authority_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(authority => authority.Id).HasName("pk_identity_authority");
        builder.Property(authority => authority.Id).ValueGeneratedNever();
        builder.Property(authority => authority.AuthorityId)
            .HasConversion<IdentityAuthorityId.EfCoreValueConverter, IdentityAuthorityId.EfCoreValueComparer>();
        builder.HasAlternateKey(authority => authority.AuthorityId).HasName("ux_identity_authority_authority_id");
        builder.Property(authority => authority.Issuer).HasMaxLength(2048).IsRequired();
        builder.DeclarePersonalData(PersonalDataCategory.None,
            "Deployment authority registration: one stable authority UUID and exact configured issuer address, without person fields.",
            nameof(IdentityAuthority.Id), nameof(IdentityAuthority.AuthorityId), nameof(IdentityAuthority.Issuer));
    }
}

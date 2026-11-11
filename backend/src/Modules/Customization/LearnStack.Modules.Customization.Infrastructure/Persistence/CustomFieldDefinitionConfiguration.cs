using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

internal sealed class CustomFieldDefinitionConfiguration : IEntityTypeConfiguration<TenantCustomFieldDef>
{
    public void Configure(EntityTypeBuilder<TenantCustomFieldDef> builder)
    {
        builder.ToTable("tenant_custom_field_defs", table =>
        {
            table.HasCheckConstraint("ck_tenant_custom_field_defs_target", "target_entity = 'Membership'");
            table.HasCheckConstraint("ck_tenant_custom_field_defs_category", "pii_category IN ('None', 'Identity', 'Behaviour', 'Sensitive')");
            table.HasCheckConstraint("ck_tenant_custom_field_defs_status", "status IN ('Draft', 'Active', 'Deprecated')");
            table.HasCheckConstraint("ck_tenant_custom_field_defs_version", "schema_version >= 1 AND sort >= 0 AND maximum_value_bytes BETWEEN 1 AND 4096");
            table.HasCheckConstraint("ck_tenant_custom_field_defs_purpose", "(pii_category = 'Sensitive' AND purpose_key IS NOT NULL AND notices_json IS NOT NULL) OR (pii_category <> 'Sensitive' AND purpose_key IS NULL AND notices_json IS NULL)");
        });
        builder.HasKey(row => row.Id).HasName("pk_tenant_custom_field_defs");
        builder.Property(row => row.Id).HasConversion<TenantCustomFieldDefId.EfCoreValueConverter, TenantCustomFieldDefId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Property(row => row.TenantId).HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>().IsRequired();
        builder.Property(row => row.Key).HasMaxLength(CustomizationKey.MaxLength).IsRequired();
        builder.Property(row => row.SchemaVersion).IsRequired();
        builder.Property(row => row.TargetEntity).HasEnumAsText().IsRequired();
        builder.Property(row => row.Status).HasEnumAsText().IsRequired();
        builder.Property(row => row.PiiCategory).HasEnumAsText().IsRequired();
        builder.Property(row => row.JsonSchema).HasColumnType("jsonb").IsRequired();
        builder.Property(row => row.LabelsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(row => row.NoticesJson).HasColumnType("jsonb");
        builder.Property(row => row.FormLabelsJson).HasColumnType("jsonb");
        builder.Property(row => row.PurposeKey).HasMaxLength(CustomFieldKey.PurposeMaxLength);
        builder.Property(row => row.IsRequired).IsRequired();
        builder.Property(row => row.Sort).IsRequired();
        builder.Property(row => row.MaximumValueBytes).IsRequired();
        builder.HasIndex(row => new { row.TenantId, row.TargetEntity, row.Key, row.SchemaVersion }).IsUnique()
            .HasDatabaseName("ux_tenant_custom_field_defs_tenant_target_key_version");
        builder.HasIndex(row => new { row.TenantId, row.TargetEntity, row.Key }).IsUnique()
            .HasFilter("status = 'Active' AND deleted_at IS NULL")
            .HasDatabaseName("ux_tenant_custom_field_defs_tenant_target_key_active");
        builder.HasIndex(row => new { row.TenantId, row.TargetEntity, row.Status })
            .HasDatabaseName("ix_tenant_custom_field_defs_tenant_target_status");
        builder.MapAuditColumns<TenantCustomFieldDef, TenantCustomFieldDefId>();
        builder.DeclarePersonalData(PersonalDataCategory.None, "Institution-authored definition structure, classification and lifecycle; payload capture is separately excluded.",
            nameof(TenantCustomFieldDef.Id), nameof(TenantCustomFieldDef.TenantId), nameof(TenantCustomFieldDef.TargetEntity),
            nameof(TenantCustomFieldDef.Key), nameof(TenantCustomFieldDef.SchemaVersion), nameof(TenantCustomFieldDef.Status),
            nameof(TenantCustomFieldDef.JsonSchema), nameof(TenantCustomFieldDef.LabelsJson), nameof(TenantCustomFieldDef.NoticesJson),
            nameof(TenantCustomFieldDef.PurposeKey), nameof(TenantCustomFieldDef.PiiCategory), nameof(TenantCustomFieldDef.IsRequired),
            nameof(TenantCustomFieldDef.Sort), nameof(TenantCustomFieldDef.MaximumValueBytes), nameof(TenantCustomFieldDef.FormLabelsJson));
    }
}

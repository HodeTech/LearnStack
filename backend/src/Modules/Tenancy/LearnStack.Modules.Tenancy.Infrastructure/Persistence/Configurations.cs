using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Shared mapping rules every Tenancy configuration applies.
/// </summary>
internal static class TenancyMapping
{
    /// <summary>
    /// Maps the six audit columns and the concurrency token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The concurrency token takes exactly three calls, and
    /// <see href="../../../../../../docs/decisions/0039-optimistic-concurrency-token.md">ADR-0039
    /// Amendment 2</see> fixes them: <c>HasDefaultValue(0L)</c> for the DDL
    /// template's <c>DEFAULT 0</c>, <c>IsConcurrencyToken()</c> for the token
    /// itself, and <c>ValueGeneratedNever()</c> because the first call otherwise
    /// leaves <c>ValueGenerated</c> at <c>OnAdd</c> — which is what
    /// <c>Aggregates_With_Optimistic_Concurrency_Map_RowVersion</c> rejects.
    /// </para>
    /// <para>
    /// <c>ValueGeneratedOnAddOrUpdate()</c> — and the equivalent
    /// <c>IsRowVersion()</c> — are the two calls that may never appear. They tell
    /// EF the database generates the value, and EF then omits the column from the
    /// <c>UPDATE</c> entirely: measured, the persisted value stays <c>0</c> for the
    /// life of the row, every <c>If-Match</c> compares equal, and a lost update
    /// succeeds while reporting success (ADR-0039 Amendment 1).
    /// </para>
    /// <para>
    /// <c>updated_at</c> / <c>updated_by</c> are nullable because
    /// <c>MarkCreated</c> stamps neither: a row that has never been changed has no
    /// updater, and <c>NOT NULL</c> would reject every insert.
    /// <c>deleted_at</c> / <c>deleted_by</c> are unconditional because
    /// <c>AuditableEntity&lt;TId&gt;</c> implements <c>ISoftDelete</c> for every
    /// aggregate, so EF maps them whether the aggregate is ever soft-deleted or
    /// not.
    /// </para>
    /// </remarks>
    public static void MapAuditColumns<TEntity, TId>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableEntity<TId>
        where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId>
    {
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.CreatedBy)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>()
            .IsRequired();
        builder.Property(x => x.UpdatedAt);
        builder.Property(x => x.UpdatedBy)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(x => x.DeletedAt);
        builder.Property(x => x.DeletedBy)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();

        builder.Property(x => x.Version)
            .HasColumnName("row_version")
            .HasDefaultValue(0L)
            .IsConcurrencyToken()
            .ValueGeneratedNever();
    }

    /// <summary>
    /// Maps a closed-set enum as <c>text</c> with the CLR name as the stored value.
    /// </summary>
    /// <remarks>
    /// Not a PostgreSQL <c>enum</c> type, whose values can only be added and never
    /// removed or reordered, and not an <c>int</c>, which makes a dump unreadable
    /// and a mistyped value indistinguishable from a valid one. The migration adds
    /// the matching <c>CHECK</c>, which is what actually bounds the column —
    /// this only decides how it is written. The store type is <c>text</c>, not
    /// <c>varchar(n)</c>: Database Standards § Column types fixes the canonical
    /// form for a closed set as "<c>text NOT NULL</c> with a
    /// <c>CHECK (col IN (…))</c>", and a length cap beside an enumerating CHECK
    /// is a second, weaker bound that can only disagree with the first.
    /// </remarks>
    public static PropertyBuilder<TEnum> HasEnumAsText<TEnum>(this PropertyBuilder<TEnum> builder)
        where TEnum : struct, Enum
        => builder.HasConversion(
                value => value.ToString(),
                text => Enum.Parse<TEnum>(text, ignoreCase: false))
            .HasColumnType("text");

}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.Property(x => x.Slug).HasMaxLength(63).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasEnumAsText().IsRequired();

        builder.Property(x => x.DefaultOrganizationId)
            .HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>();

        // Globally unique, not per tenant: a slug appears in hostnames and is
        // public by construction, which is why the leak a unique index causes
        // (PostgreSQL enforces them with row security bypassed) is accepted here
        // and nowhere else.
        builder.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_tenants_slug");

        builder.MapAuditColumns<Tenant, TenantId>();
    }
}

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.Slug).HasMaxLength(63).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CustomSubdomain).HasMaxLength(253);
        builder.Property(x => x.Status).HasEnumAsText().IsRequired();

        builder.Property(x => x.ReportingParentId)
            .HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>();

        builder.HasIndex(x => new { x.TenantId, x.Slug })
            .IsUnique()
            .HasDatabaseName("ux_organizations_tenant_id_slug");

        // Exists solely so tenants.default_organization_id — and every future
        // org-scoped child — can carry a composite foreign key into this table.
        // Looks redundant beside the primary key and is not: a single-column FK
        // between two tenant-owned tables lets tenant A reference tenant B's row,
        // because referential-integrity checks run with row security bypassed.
        builder.HasIndex(x => new { x.TenantId, x.Id })
            .IsUnique()
            .HasDatabaseName("ux_organizations_tenant_id_id");

        // fk_organizations_reporting_parent's own columns. Standards 05 § Indexes
        // says index every foreign key, and the two indexes above only lead with
        // tenant_id — neither can serve the ON DELETE RESTRICT scan that looks for
        // children of the organization being deleted.
        builder.HasIndex(x => new { x.TenantId, x.ReportingParentId })
            .HasDatabaseName("ix_organizations_tenant_id_reporting_parent_id");

        builder.MapAuditColumns<Organization, OrganizationId>();
    }
}

internal sealed class TenantDomainConfiguration : IEntityTypeConfiguration<TenantDomain>
{
    public void Configure(EntityTypeBuilder<TenantDomain> builder)
    {
        builder.ToTable("tenant_domains");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion<TenantDomainId.EfCoreValueConverter, TenantDomainId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.Host).HasMaxLength(253).IsRequired();
        builder.Property(x => x.Kind).HasEnumAsText().IsRequired();
        builder.Property(x => x.Status).HasEnumAsText().IsRequired();
        builder.Property(x => x.VerifiedAt);
        builder.Property(x => x.VerificationAttempts).HasDefaultValue(0).IsRequired();
        builder.Property(x => x.LastVerificationError).HasMaxLength(1000);

        // Globally unique, and the second — with `tenants.slug` — of the two
        // cases Database Standards § Table classes sanctions on a tenant-owned
        // table: a host resolving to two tenants is unresolvable regardless of who
        // owns it. It costs the same leak the slug costs, because PostgreSQL
        // enforces unique indexes with row security bypassed, so a duplicate
        // insert reveals that *some* tenant already claims the host.
        //
        // Partial on `deleted_at IS NULL`, which is not decoration: without the
        // predicate a soft-deleted claim keeps the name forever, and ADR-0036
        // § Custom domains contemplates a released-then-re-registered domain —
        // a lifecycle a table-wide unique makes unimplementable.
        builder.HasIndex(x => x.Host)
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_tenant_domains_host");
        builder.HasIndex(x => x.TenantId).HasDatabaseName("ix_tenant_domains_tenant_id");

        builder.MapAuditColumns<TenantDomain, TenantDomainId>();
    }
}

internal sealed class TenantLocaleConfiguration : IEntityTypeConfiguration<TenantLocale>
{
    public void Configure(EntityTypeBuilder<TenantLocale> builder)
    {
        builder.ToTable("tenant_locales");

        // Composite natural key, no surrogate id: a second row for the same
        // tenant and locale is not a second locale, it is a duplicate.
        builder.HasKey(x => new { x.TenantId, x.Locale })
            .HasName("pk_tenant_locales");

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.Locale).HasMaxLength(35).IsRequired();
        builder.Property(x => x.IsDefault).IsRequired();
        builder.Property(x => x.IsEnabled).HasDefaultValue(true).IsRequired();
        builder.Property(x => x.Sort).HasDefaultValue((short)0).IsRequired();
    }
}

internal sealed class TenantSettingConfiguration : IEntityTypeConfiguration<TenantSetting>
{
    public void Configure(EntityTypeBuilder<TenantSetting> builder)
    {
        builder.ToTable("tenant_settings");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion<TenantSettingId.EfCoreValueConverter, TenantSettingId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.OrganizationId)
            .HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>();

        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Value).HasColumnType("jsonb").IsRequired();

        // NULLS NOT DISTINCT, expressed in the model rather than patched into the
        // migration afterwards: organization_id is null on every tenant-wide row,
        // and a standard UNIQUE treats nulls as distinct — so without this a tenant
        // could hold unlimited duplicate tenant-wide rows for one key, which is
        // precisely the set a single-organization tenant creates exclusively, and
        // resolution would pick one arbitrarily.
        builder.HasIndex(x => new { x.TenantId, x.OrganizationId, x.Key })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_tenant_settings_tenant_id_organization_id_key");

        builder.HasIndex(x => new { x.TenantId, x.OrganizationId })
            .HasDatabaseName("ix_tenant_settings_tenant_id_organization_id");

        builder.MapAuditColumns<TenantSetting, TenantSettingId>();
    }
}

internal sealed class TenantFeatureFlagConfiguration : IEntityTypeConfiguration<TenantFeatureFlag>
{
    public void Configure(EntityTypeBuilder<TenantFeatureFlag> builder)
    {
        builder.ToTable("tenant_feature_flags");
        builder.HasKey(x => new { x.TenantId, x.Key }).HasName("pk_tenant_feature_flags");

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Value).HasColumnType("jsonb").IsRequired();

        // DEFAULT now(), as 21-feature-flags.md declares it. A flag row written by
        // raw SQL that omits the column would otherwise violate NOT NULL.
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsRequired();

        builder.Property(x => x.UpdatedBy)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>()
            .IsRequired();
    }
}

internal sealed class PlatformEntitlementConfiguration : IEntityTypeConfiguration<PlatformEntitlement>
{
    public void Configure(EntityTypeBuilder<PlatformEntitlement> builder)
    {
        builder.ToTable("platform_entitlement_cache");

        // One row per tenant; the tenant id IS the key.
        builder.HasKey(x => x.TenantId).HasName("pk_platform_entitlement_cache");

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.Property(x => x.PlanCode).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Features).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Limits).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Compliance).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.ValidUntil).IsRequired();
        builder.Property(x => x.GraceUntil);
        builder.Property(x => x.Generation).HasDefaultValue(1L).IsRequired();

        // DEFAULT now(), as 21-feature-flags.md declares it — the same reason as
        // tenant_feature_flags.updated_at, and the same provisioning insert.
        builder.Property(x => x.RefreshedAt).HasDefaultValueSql("now()").IsRequired();

        // Closed set, so text + CHECK rather than a length cap.
        builder.Property(x => x.Source).HasColumnType("text").IsRequired();
    }
}

internal sealed class PlatformHostMappingConfiguration : IEntityTypeConfiguration<PlatformHostMapping>
{
    public void Configure(EntityTypeBuilder<PlatformHostMapping> builder)
    {
        builder.ToTable("platform_host_to_tenant");

        // The host is the key: one answer per host, enforced by the primary key
        // rather than by a unique index over a surrogate.
        builder.HasKey(x => x.Host).HasName("pk_platform_host_to_tenant");

        builder.Property(x => x.Host).HasMaxLength(253).IsRequired();

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.OrganizationId)
            .HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>();

        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.IsPubliclyLive).IsRequired();

        // Two jobs, one index. A tenant listing its own hosts is the second of the
        // two read paths the policy admits (the first, by host, is the key), and
        // tenant_id leads, so the composite serves it. The organization column is
        // there because fk_platform_host_to_tenant_organization is composite on
        // (tenant_id, organization_id) and Standards 05 § Indexes says index every
        // foreign key — a leading-column-only index does not serve the ON DELETE
        // RESTRICT scan, it just narrows it to the tenant.
        builder.HasIndex(x => new { x.TenantId, x.OrganizationId })
            .HasDatabaseName("ix_platform_host_to_tenant_tenant_id_organization_id");
    }
}

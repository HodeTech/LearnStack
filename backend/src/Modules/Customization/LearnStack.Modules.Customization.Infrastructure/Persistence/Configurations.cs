using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

/// <summary>
/// Shared mapping rules every Customization configuration applies.
/// </summary>
internal static class CustomizationMapping
{
    /// <summary>The width a customization key column maps.</summary>
    internal const int KeyLength = CustomizationKey.MaxLength;

    /// <summary>The width a composite renderer key column maps.</summary>
    internal const int RendererKeyLength = 100;

    /// <summary>
    /// Maps the six audit columns and the concurrency token, exactly as the
    /// Tenancy schema does.
    /// </summary>
    /// <remarks>
    /// Three calls on the token and no others, per
    /// <see href="../../../../../../docs/decisions/0039-optimistic-concurrency-token.md">ADR-0039
    /// Amendment 2</see>: <c>HasDefaultValue(0L)</c> for the DDL's <c>DEFAULT 0</c>,
    /// <c>IsConcurrencyToken()</c> for the token itself, and
    /// <c>ValueGeneratedNever()</c> because the first call otherwise leaves
    /// <c>ValueGenerated</c> at <c>OnAdd</c>.
    /// <c>ValueGeneratedOnAddOrUpdate()</c> and <c>IsRowVersion()</c> are the two
    /// that may never appear — they make EF omit the column from the
    /// <c>UPDATE</c>, so the persisted value stays <c>0</c> and every
    /// <c>If-Match</c> compares equal.
    /// </remarks>
    internal static void MapAuditColumns<TEntity, TId>(this EntityTypeBuilder<TEntity> builder)
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
    /// Maps the fields every versioned definition carries, and the two indexes
    /// <see href="../../../../../../docs/roadmap/phase-04-cms-media-pages.md">Phase 04
    /// § Customization Key Shape</see> fixes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UNIQUE (tenant_id, key, schema_version)</c> identifies one immutable
    /// revision; the partial <c>UNIQUE (tenant_id, key) WHERE status = 'active'</c>
    /// keeps at most one live definition per concept. <c>UNIQUE (tenant_id, key)</c>
    /// alone would reject the second revision of any key, which is the constraint
    /// that made the first breaking change ADR-0013 requires impossible.
    /// </para>
    /// <para>
    /// The partial index is what actually holds the one-live-revision rule: the
    /// aggregate cannot see its siblings, so the command that publishes a successor
    /// deprecates the incumbent in the same transaction and this catches the case
    /// where it did not.
    /// </para>
    /// </remarks>
    internal static void MapDefinition<TEntity, TId>(
        this EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : CustomizationDefinition<TId>
        where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId>
    {
        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.Key).HasMaxLength(KeyLength).IsRequired();
        builder.Property(x => x.SchemaVersion).IsRequired();
        builder.Property(x => x.SchemaRevision).HasDefaultValue(0).IsRequired();
        builder.Property(x => x.Status).HasEnumAsText().IsRequired();
        builder.Property(x => x.DisplayName).HasLocalizedText().IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.Key, x.SchemaVersion })
            .IsUnique()
            .HasDatabaseName($"ux_{table}_tenant_id_key_schema_version");

        // Raw SQL, evaluated after the snake-case convention runs — so `status`
        // and the stored enum name, not `Status` and not an ordinal.
        builder.HasIndex(x => new { x.TenantId, x.Key })
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName($"ux_{table}_tenant_id_key_active");
    }

    /// <summary>
    /// Maps a closed-set enum as <c>text</c> with the CLR name as the stored value.
    /// </summary>
    /// <remarks>
    /// Not a PostgreSQL <c>enum</c> type, whose values can only be added and never
    /// removed or reordered, and not an <c>int</c>, which makes a dump unreadable
    /// and a mistyped value indistinguishable from a valid one. The migration adds
    /// the matching <c>CHECK</c>, which is what actually bounds the column.
    /// </remarks>
    internal static PropertyBuilder<TEnum> HasEnumAsText<TEnum>(this PropertyBuilder<TEnum> builder)
        where TEnum : struct, Enum
        => builder.HasConversion(
                value => value.ToString(),
                text => Enum.Parse<TEnum>(text, ignoreCase: false))
            .HasColumnType("text");

    /// <summary>
    /// Maps a <see cref="LocalizedText"/> to one <c>jsonb</c> column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pattern B (<see href="../../../../../../docs/standards/08-localization.md">Localization
    /// Standards</see>): a short atomic string listed many at a time, where joining
    /// a translation table would be an N+1 for a label.
    /// </para>
    /// <para>
    /// The comparer is not decoration. EF's default for a converted reference type
    /// is reference equality, so a materialized value would compare unequal to
    /// itself on every <c>SaveChanges</c> and every row would be written back on
    /// every save — including rows nothing touched, each one advancing its
    /// concurrency token. <see cref="LocalizedText"/> is immutable, so the snapshot
    /// is the instance itself.
    /// </para>
    /// </remarks>
    internal static PropertyBuilder<LocalizedText> HasLocalizedText(
        this PropertyBuilder<LocalizedText> builder)
        => builder
            .HasConversion(
                new ValueConverter<LocalizedText, string>(
                    value => value.ToJson(),
                    json => LocalizedText.FromJson(json)),
                new ValueComparer<LocalizedText>(
                    (left, right) => left != null && right != null && left.Equals(right),
                    value => value.GetHashCode(),
                    value => value))
            .HasColumnType("jsonb");
}

internal sealed class TenantContentTypeConfiguration : IEntityTypeConfiguration<TenantContentType>
{
    public void Configure(EntityTypeBuilder<TenantContentType> builder)
    {
        builder.ToTable("tenant_content_types");
        builder.HasKey(x => x.Id).HasName("pk_tenant_content_types");

        builder.Property(x => x.Id)
            .HasConversion<TenantContentTypeId.EfCoreValueConverter, TenantContentTypeId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.MapDefinition<TenantContentType, TenantContentTypeId>("tenant_content_types");

        // jsonb rather than text: the column holds a JSON document, PostgreSQL
        // refuses a malformed one, and a later phase reads inside it. The aggregate
        // refuses malformed JSON at the factory so the 22P02 never has to explain
        // itself three layers from the call.
        builder.Property(x => x.JsonSchema).HasColumnType("jsonb").IsRequired();

        builder.Property(x => x.RendererKey)
            .HasMaxLength(CustomizationMapping.RendererKeyLength)
            .IsRequired();

        builder.MapAuditColumns<TenantContentType, TenantContentTypeId>();
    }
}

internal sealed class TenantLevelTaxonomyConfiguration : IEntityTypeConfiguration<TenantLevelTaxonomy>
{
    public void Configure(EntityTypeBuilder<TenantLevelTaxonomy> builder)
    {
        builder.ToTable("tenant_level_taxonomies");
        builder.HasKey(x => x.Id).HasName("pk_tenant_level_taxonomies");

        builder.Property(x => x.Id)
            .HasConversion<TenantLevelTaxonomyId.EfCoreValueConverter, TenantLevelTaxonomyId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.MapDefinition<TenantLevelTaxonomy, TenantLevelTaxonomyId>("tenant_level_taxonomies");
        builder.MapAuditColumns<TenantLevelTaxonomy, TenantLevelTaxonomyId>();

        // The containment the aggregate boundary decides, expressed for EF — and
        // NOT as an owned type. An owned mapping reads like the natural way to say
        // "part of the root" and silently removes the child's own query filter,
        // because the filter sweep skips `entityType.IsOwned()`. The child would
        // then lose the EF half of the four-layer isolation while looking correct.
        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(item => new { item.TenantId, item.TaxonomyKey, item.SchemaVersion })
            .HasPrincipalKey(taxonomy => new
            {
                taxonomy.TenantId,
                taxonomy.Key,
                taxonomy.SchemaVersion,
            })
            .HasConstraintName("fk_tenant_level_taxonomy_items_taxonomy")
            .OnDelete(DeleteBehavior.Cascade);

        // The collection is exposed through a projecting property, so EF is told
        // to read and write the backing field. Without this it would call the
        // getter, get a fresh sorted List every time, and track changes against a
        // copy nothing else holds.
        builder.Navigation(x => x.Items)
            .HasField("_items")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class TenantLevelTaxonomyItemConfiguration
    : IEntityTypeConfiguration<TenantLevelTaxonomyItem>
{
    public void Configure(EntityTypeBuilder<TenantLevelTaxonomyItem> builder)
    {
        builder.ToTable("tenant_level_taxonomy_items");

        // Composite natural key, no surrogate id — the same shape and the same
        // argument as `TenantLocale`. The version is in the key because items
        // belong to a revision, not to a concept: two revisions of `cefr` may
        // disagree about which bands exist.
        builder.HasKey(x => new { x.TenantId, x.TaxonomyKey, x.SchemaVersion, x.Key })
            .HasName("pk_tenant_level_taxonomy_items");

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.TaxonomyKey).HasMaxLength(CustomizationMapping.KeyLength).IsRequired();
        builder.Property(x => x.SchemaVersion).IsRequired();
        builder.Property(x => x.Key).HasMaxLength(CustomizationMapping.KeyLength).IsRequired();
        builder.Property(x => x.Sort).IsRequired();
        builder.Property(x => x.DisplayName).HasLocalizedText().IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("jsonb");

        // The aggregate refuses a duplicate sort with a message a tenant admin can
        // read; this is what holds it across two concurrent transactions, where an
        // in-memory check on two aggregates both pass.
        builder.HasIndex(x => new { x.TenantId, x.TaxonomyKey, x.SchemaVersion, x.Sort })
            .IsUnique()
            .HasDatabaseName("ux_tenant_level_taxonomy_items_taxonomy_sort");
    }
}

internal sealed class CustomizationGenerationConfiguration
    : IEntityTypeConfiguration<CustomizationGeneration>
{
    public void Configure(EntityTypeBuilder<CustomizationGeneration> builder)
    {
        builder.ToTable("customization_generations");

        // The tenant IS the identity: one counter per tenant, and a second row for
        // one tenant is not a second counter.
        builder.HasKey(x => x.TenantId).HasName("pk_customization_generations");

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.Property(x => x.Generation).HasDefaultValue(1L).IsRequired();
    }
}

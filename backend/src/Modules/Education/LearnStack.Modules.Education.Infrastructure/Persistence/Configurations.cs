using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Education.Infrastructure.Persistence;

internal static class EducationMapping
{
    internal static void MapScope<TEntity>(this EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : class, IOrganizationScoped
    {
        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();
        builder.Property(x => x.OrganizationId)
            .HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>();
        builder.HasIndex(x => new { x.TenantId, x.OrganizationId })
            .HasDatabaseName($"ix_{table}_tenant_id_organization_id");
    }

    // ADR-0048 fixes lowercase storage independently of CLR enum spelling.
    internal static void MapStatus(this PropertyBuilder<PublicationStatus> builder)
        => builder.HasConversion(
                value => value.ToString().ToLowerInvariant(),
                value => Enum.Parse<PublicationStatus>(value, true))
            .HasColumnType("text")
            .IsRequired();

    internal static string SlugCheck(string column)
        => $"{column} ~ '^[a-z0-9]+(-[a-z0-9]+)*$' "
            + $"AND {column} !~ '^[0-9a-f]{{32}}$' "
            + $"AND {column} !~ '^[0-9a-f]{{8}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{12}}$'";
}

internal sealed class CourseConfiguration : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> builder)
    {
        builder.ToTable("courses", table =>
        {
            table.HasCheckConstraint("ck_courses_slug_key_format", EducationMapping.SlugCheck("slug_key"));
            table.HasCheckConstraint("ck_courses_status", "status IN ('draft', 'published')");
            table.HasCheckConstraint("ck_courses_level_reference", """
                (level_taxonomy_key IS NULL AND level_taxonomy_schema_version IS NULL AND level_band_key IS NULL)
                OR (level_taxonomy_key IS NOT NULL AND level_taxonomy_schema_version IS NOT NULL
                    AND level_taxonomy_schema_version > 0 AND level_band_key IS NOT NULL)
                """);
        });
        builder.HasKey(x => x.Id).HasName("pk_courses");
        builder.Property(x => x.Id)
            .HasConversion<CourseId.EfCoreValueConverter, CourseId.EfCoreValueComparer>()
            .ValueGeneratedNever();
        builder.MapScope("courses");
        builder.HasAlternateKey(x => new { x.TenantId, x.Id }).HasName("ux_courses_tenant_id_id");
        builder.Property(x => x.SlugKey).HasMaxLength(EducationSlug.MaxLength).IsRequired();
        builder.Property(x => x.Status).MapStatus();
        builder.Property(x => x.LevelTaxonomyKey).HasMaxLength(EducationPinKey.MaxLength);
        builder.Property(x => x.LevelTaxonomySchemaVersion);
        builder.Property(x => x.LevelBandKey).HasMaxLength(EducationPinKey.MaxLength);
        builder.MapAuditColumns<Course, CourseId>();
        builder.HasIndex(x => new { x.TenantId, x.SlugKey })
            .IsUnique().HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_courses_tenant_id_slug_key");
        builder.HasIndex(x => new { x.TenantId, x.OrganizationId, x.CreatedAt, x.Id })
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ix_courses_tenant_id_organization_id_created_at_id");

        // Containment with an ordinary entity preserves the satellite's own filter.
        builder.HasMany(x => x.Translations).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.CourseId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .HasConstraintName("fk_course_translations_course")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Translations).HasField("_translations")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        builder.ToTable("lessons", table =>
        {
            table.HasCheckConstraint("ck_lessons_status", "status IN ('draft', 'published')");
            table.HasCheckConstraint("ck_lessons_sort", "sort >= 0");
            table.HasCheckConstraint("ck_lessons_content_type_schema_version", "content_type_schema_version > 0");
        });
        builder.HasKey(x => x.Id).HasName("pk_lessons");
        builder.Property(x => x.Id)
            .HasConversion<LessonId.EfCoreValueConverter, LessonId.EfCoreValueComparer>()
            .ValueGeneratedNever();
        builder.MapScope("lessons");
        builder.HasAlternateKey(x => new { x.TenantId, x.Id }).HasName("ux_lessons_tenant_id_id");
        builder.Property(x => x.CourseId)
            .HasConversion<CourseId.EfCoreValueConverter, CourseId.EfCoreValueComparer>()
            .IsRequired();
        builder.Property(x => x.Sort).IsRequired();
        builder.Property(x => x.Status).MapStatus();
        builder.Property(x => x.ContentTypeKey).HasMaxLength(EducationPinKey.MaxLength).IsRequired();
        builder.Property(x => x.ContentTypeSchemaVersion).IsRequired();
        builder.MapAuditColumns<Lesson, LessonId>();
        builder.HasOne<Course>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.CourseId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .HasConstraintName("fk_lessons_course")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.TenantId, x.CourseId })
            .HasDatabaseName("ix_lessons_tenant_id_course_id");
        builder.HasIndex(x => new { x.TenantId, x.CourseId, x.Sort, x.Id })
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ix_lessons_tenant_id_course_id_sort_id");
        builder.HasMany(x => x.Translations).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.LessonId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .HasConstraintName("fk_lesson_translations_lesson")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Translations).HasField("_translations")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CourseTranslationConfiguration : IEntityTypeConfiguration<CourseTranslation>
{
    public void Configure(EntityTypeBuilder<CourseTranslation> builder)
    {
        builder.ToTable("course_translations", table => table.HasCheckConstraint(
            "ck_course_translations_slug_format", EducationMapping.SlugCheck("slug")));
        builder.HasKey(x => new { x.CourseId, x.Locale }).HasName("pk_course_translations");
        builder.Property(x => x.CourseId)
            .HasConversion<CourseId.EfCoreValueConverter, CourseId.EfCoreValueComparer>()
            .ValueGeneratedNever();
        builder.MapScope("course_translations");
        builder.Property(x => x.Locale).HasMaxLength(LocaleTag.MaxLength).IsRequired();
        builder.Property(x => x.Title).HasColumnType("text").IsRequired();
        builder.Property(x => x.Summary).HasColumnType("text");
        builder.Property(x => x.Slug).HasMaxLength(EducationSlug.MaxLength).IsRequired();
        builder.HasAlternateKey(x => new { x.TenantId, x.Locale, x.Slug })
            .HasName("ux_course_translations_tenant_id_locale_slug");
        builder.HasIndex(x => new { x.TenantId, x.CourseId })
            .HasDatabaseName("ix_course_translations_tenant_id_course_id");
    }
}

internal sealed class LessonTranslationConfiguration : IEntityTypeConfiguration<LessonTranslation>
{
    public void Configure(EntityTypeBuilder<LessonTranslation> builder)
    {
        builder.ToTable("lesson_translations", table =>
        {
            table.HasCheckConstraint("ck_lesson_translations_slug_format", EducationMapping.SlugCheck("slug"));
            table.HasCheckConstraint("ck_lesson_translations_body_object", "jsonb_typeof(body) = 'object'");
        });
        builder.HasKey(x => new { x.LessonId, x.Locale }).HasName("pk_lesson_translations");
        builder.Property(x => x.LessonId)
            .HasConversion<LessonId.EfCoreValueConverter, LessonId.EfCoreValueComparer>()
            .ValueGeneratedNever();
        builder.MapScope("lesson_translations");
        builder.Property(x => x.Locale).HasMaxLength(LocaleTag.MaxLength).IsRequired();
        builder.Property(x => x.Title).HasColumnType("text").IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(EducationSlug.MaxLength).IsRequired();
        builder.Property(x => x.Body).HasColumnType("jsonb").IsRequired();
        builder.HasAlternateKey(x => new { x.TenantId, x.Locale, x.Slug })
            .HasName("ux_lesson_translations_tenant_id_locale_slug");
        builder.HasIndex(x => new { x.TenantId, x.LessonId })
            .HasDatabaseName("ix_lesson_translations_tenant_id_lesson_id");
    }
}

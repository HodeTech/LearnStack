using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Tools.Seeder;

/// <summary>Deep immutable copies of the console's fixed data; requests never supply these pins.</summary>
public sealed class FixedSeedContentContinuationSource : ISeedCustomizationContinuationSource, ISeedEducationContinuationSource
{
    private readonly ImmutableDictionary<TenantId, SeedCustomizationDeclaration> _customization;
    private readonly ImmutableDictionary<TenantId, SeedEducationDeclaration> _education;

    public FixedSeedContentContinuationSource()
    {
        _customization = SeedData.All.Where(tenant => tenant.Curriculum is not null)
            .ToImmutableDictionary(tenant => tenant.TenantId, tenant =>
            {
                var content = tenant.Curriculum!;
                var card = BuiltInCustomizations.Card;
                var plain = BuiltInCustomizations.Plain;
                return new SeedCustomizationDeclaration(tenant.TenantId,
                    new(null, card.Key, BuiltInCustomizations.SchemaVersion, Map(card.DisplayName), card.JsonSchema, card.RendererKey),
                    new(null, plain.Key, BuiltInCustomizations.SchemaVersion, Map(plain.DisplayName),
                        [.. plain.Bands.Select(band => new SeedTaxonomyItemDeclaration(band.Key, Map(band.DisplayName), band.Sort, null))]),
                    new(content.ContentType.Id, content.ContentType.Key, content.ContentType.SchemaVersion,
                        Map(content.ContentType.DisplayName), content.ContentType.JsonSchema, content.ContentType.RendererKey),
                    new(content.Taxonomy.Id, content.Taxonomy.Key, content.Taxonomy.SchemaVersion, Map(content.Taxonomy.DisplayName),
                        [.. content.Taxonomy.Bands.Select(band => new SeedTaxonomyItemDeclaration(band.Key, Map(band.DisplayName), band.Sort, band.Metadata))]));
            });
        _education = SeedData.All.Where(tenant => tenant.Curriculum is not null)
            .ToImmutableDictionary(tenant => tenant.TenantId, tenant => new SeedEducationDeclaration(tenant.TenantId,
                [.. tenant.Curriculum!.Courses.Select(course => new SeedCourseDeclaration(course.Id, course.OrganizationId,
                    course.SlugKey, course.ContentAccess, course.Status, course.LevelTaxonomyKey, course.LevelTaxonomySchemaVersion,
                    course.LevelBandKey, [.. course.Translations.Select(value => new CourseTranslationSeedDto(value.Locale,
                        value.Title, value.Summary, value.Slug))], [.. course.Lessons.Select(lesson => new SeedLessonDeclaration(
                            lesson.Id, lesson.Sort, lesson.Status, lesson.ContentTypeKey, lesson.ContentTypeSchemaVersion,
                            [.. lesson.Translations.Select(value => new LessonTranslationSeedDto(value.Locale, value.Title, value.Slug, value.Body))]))]))]));
    }

    SeedCustomizationDeclaration? ISeedCustomizationContinuationSource.ForTenant(TenantId tenantId) => _customization.GetValueOrDefault(tenantId);
    SeedEducationDeclaration? ISeedEducationContinuationSource.ForTenant(TenantId tenantId) => _education.GetValueOrDefault(tenantId);

    private static ImmutableDictionary<string, string> Map(IReadOnlyDictionary<string, string> value) =>
        value.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

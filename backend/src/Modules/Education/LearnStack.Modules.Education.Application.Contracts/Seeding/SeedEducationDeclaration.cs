using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Education.Application.Contracts.Seeding;

/// <summary>Immutable fixed content pins captured independently of the submitted operation.</summary>
public sealed record SeedEducationDeclaration(TenantId TenantId, ImmutableArray<SeedCourseDeclaration> Courses);

public sealed record SeedCourseDeclaration(Guid Id, OrganizationId? OrganizationId, string SlugKey,
    string ContentAccess, string Status, string? LevelTaxonomyKey, int? LevelTaxonomySchemaVersion,
    string? LevelBandKey, ImmutableArray<CourseTranslationSeedDto> Translations,
    ImmutableArray<SeedLessonDeclaration> Lessons);

public sealed record SeedLessonDeclaration(Guid Id, int Sort, string Status, string ContentTypeKey,
    int ContentTypeSchemaVersion, ImmutableArray<LessonTranslationSeedDto> Translations);

public interface ISeedEducationContinuationSource
{
    SeedEducationDeclaration? ForTenant(TenantId tenantId);
}

public sealed class DenyAllSeedEducationContinuationSource : ISeedEducationContinuationSource
{
    public SeedEducationDeclaration? ForTenant(TenantId tenantId) => null;
}

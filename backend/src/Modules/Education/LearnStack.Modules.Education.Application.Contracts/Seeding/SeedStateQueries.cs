using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Education.Application.Contracts.Seeding;

// Trusted contextual verification only: no public marker, endpoint or tenant input.
public sealed record SeedLookup<T>(T? State) where T : class;

public sealed record CourseTranslationSeedDto(string Locale, string Title, string? Summary, string Slug);
public sealed record LessonTranslationSeedDto(string Locale, string Title, string Slug, string Body);
public sealed record CourseSeedDto(Guid Id, TenantId TenantId, OrganizationId? OrganizationId,
    string SlugKey, string Status, string ContentAccess, string? LevelTaxonomyKey,
    int? LevelTaxonomySchemaVersion, string? LevelBandKey, long Version,
    ImmutableArray<CourseTranslationSeedDto> Translations);
public sealed record LessonSeedDto(Guid Id, TenantId TenantId, OrganizationId? OrganizationId,
    Guid CourseId, int Sort, string Status, string ContentTypeKey, int ContentTypeSchemaVersion,
    long Version, ImmutableArray<LessonTranslationSeedDto> Translations);

public sealed record GetCourseSeedStateQuery(Guid CourseId) : IRequest<Result<SeedLookup<CourseSeedDto>>>;
public sealed record GetLessonSeedStateQuery(Guid LessonId) : IRequest<Result<SeedLookup<LessonSeedDto>>>;

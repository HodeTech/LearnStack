using System.Collections.Immutable;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Domain;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Education.Infrastructure.Persistence;

public sealed class EducationSeedStateReader(EducationDbContext context) : ISeedStateReader
{
    public async Task<CourseSeedDto?> ReadCourseAsync(CourseId courseId, CancellationToken cancellationToken)
    {
        var row = await context.Courses.AsNoTracking().Include(course => course.Translations)
            .SingleOrDefaultAsync(course => course.Id == courseId && course.DeletedAt == null, cancellationToken);
        return row is null ? null : new CourseSeedDto(row.Id.Value, row.TenantId, row.OrganizationId,
            row.SlugKey, row.Status.ToString(), row.ContentAccess == CourseContentAccess.Public ? "public" : "enrollment_required",
            row.LevelTaxonomyKey, row.LevelTaxonomySchemaVersion, row.LevelBandKey, row.Version,
            row.Translations.OrderBy(translation => translation.Locale, StringComparer.Ordinal)
                .Select(translation => new CourseTranslationSeedDto(translation.Locale, translation.Title,
                    translation.Summary, translation.Slug)).ToImmutableArray());
    }

    public async Task<LessonSeedDto?> ReadLessonAsync(LessonId lessonId, CancellationToken cancellationToken)
    {
        var row = await context.Lessons.AsNoTracking().Include(lesson => lesson.Translations)
            .SingleOrDefaultAsync(lesson => lesson.Id == lessonId && lesson.DeletedAt == null, cancellationToken);
        return row is null ? null : new LessonSeedDto(row.Id.Value, row.TenantId, row.OrganizationId,
            row.CourseId.Value, row.Sort, row.Status.ToString(), row.ContentTypeKey, row.ContentTypeSchemaVersion,
            row.Version, row.Translations.OrderBy(translation => translation.Locale, StringComparer.Ordinal)
                .Select(translation => new LessonTranslationSeedDto(translation.Locale, translation.Title,
                    translation.Slug, translation.Body)).ToImmutableArray());
    }
}

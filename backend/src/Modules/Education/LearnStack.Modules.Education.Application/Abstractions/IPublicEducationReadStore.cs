using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Domain;

namespace LearnStack.Modules.Education.Application.Abstractions;

public sealed record CatalogContinuation(DateTimeOffset CreatedAt, CourseId Id);
public sealed record OutlineContinuation(CourseId Parent, int Sort, LessonId Id);

/// <summary>Internal projection rows; never serialized as a public response.</summary>
public sealed record PublicCourseReadRow(
    CourseId Id, DateTimeOffset CreatedAt, string Slug, string Title, string? Summary,
    CourseContentAccess ContentAccess, string? TaxonomyKey, int? TaxonomyVersion, string? BandKey);
public sealed record PublicOutlineReadRow(LessonId Id, string Slug, string Title, int Sort);
public sealed record PublicLessonReadRow(
    CourseId CourseId, string CourseSlug, string CourseTitle, LessonId Id, string Slug, string Title,
    string ContentTypeKey, int ContentTypeVersion, string Body);

/// <summary>Filtered exact-locale source reads on the announced read-only transaction.</summary>
public interface IPublicEducationReadStore
{
    Task<IReadOnlyList<PublicCourseReadRow>> ReadCatalogAsync(string locale, CatalogContinuation? after, int take, CancellationToken cancellationToken);
    Task<PublicCourseReadRow?> ReadCourseAsync(string slug, string locale, CancellationToken cancellationToken);
    Task<IReadOnlyList<PublicOutlineReadRow>> ReadOutlineAsync(CourseId parent, string locale, OutlineContinuation? after, int take, CancellationToken cancellationToken);
    Task<PublicLessonReadRow?> ReadLessonAsync(string courseSlug, string lessonSlug, string locale, CancellationToken cancellationToken);
    Task<IReadOnlyList<PublicCourseAlternate>> ReadCourseAlternatesAsync(CourseId parent, string locale, string[] enabled, CancellationToken cancellationToken);
    Task<IReadOnlyList<PublicLessonAlternate>> ReadLessonAlternatesAsync(CourseId parent, LessonId lesson, string locale, string[] enabled, CancellationToken cancellationToken);
}

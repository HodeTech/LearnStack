using System.Linq.Expressions;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Modules.Education.Infrastructure.Persistence;

/// <summary>Fresh eligibility in SQL, intersecting host scope, ordinary filters and RLS.</summary>
public sealed class PublicEducationReadStore(EducationDbContext db, ITenantContext context, IUnitOfWork unit) : IPublicEducationReadStore
{
    public async Task<IReadOnlyList<PublicCourseReadRow>> ReadCatalogAsync(
        string locale, CatalogContinuation? after, int take, CancellationToken cancellationToken)
    {
        var host = Admit();
        IQueryable<Course> source = db.Courses;
        if (after is { } anchor)
            source = db.Courses.FromSqlInterpolated($"""
                SELECT * FROM courses WHERE tenant_id={host.TenantId.Value}
                AND (organization_id IS NULL OR organization_id={host.OrganizationId?.Value})
                AND (created_at,id)>({anchor.CreatedAt},{anchor.Id.Value})
                """);
        return await Courses(locale, source).OrderBy(row => row.Course.CreatedAt).ThenBy(row => row.Course.Id)
            .Take(take).Select(CourseRow).TagWith("P02d-4 catalog").ToArrayAsync(cancellationToken);
    }

    public Task<PublicCourseReadRow?> ReadCourseAsync(string slug, string locale, CancellationToken cancellationToken) =>
        Courses(locale, db.Courses).Where(row => row.Translation.Slug == slug)
            .Select(CourseRow).TagWith("P02d-4 course marketing").SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PublicOutlineReadRow>> ReadOutlineAsync(
        CourseId parent, string locale, OutlineContinuation? after, int take, CancellationToken cancellationToken)
    {
        var host = Admit();
        IQueryable<Lesson> source = db.Lessons;
        if (after is { } anchor)
            source = db.Lessons.FromSqlInterpolated($"""
                SELECT * FROM lessons WHERE tenant_id={host.TenantId.Value} AND course_id={parent.Value}
                AND (organization_id IS NULL OR organization_id={host.OrganizationId?.Value})
                AND (sort,id)>({anchor.Sort},{anchor.Id.Value})
                """);
        var parents = Courses(locale, db.Courses).Where(row => row.Course.Id == parent
            && row.Course.ContentAccess == CourseContentAccess.Public);
        var lessons = Lessons(source);
        var translations = LessonTranslations(locale);
        return await (from course in parents
                      join lesson in lessons on course.Course.Id equals lesson.CourseId
                      join translation in translations on lesson.Id equals translation.LessonId
                      orderby lesson.Sort, lesson.Id
                      select new PublicOutlineReadRow(lesson.Id, translation.Slug, translation.Title, lesson.Sort))
            .Take(take).TagWith("P02d-4 course outline").ToArrayAsync(cancellationToken);
    }

    public Task<PublicLessonReadRow?> ReadLessonAsync(string courseSlug, string lessonSlug, string locale, CancellationToken cancellationToken)
    {
        // The SELECT that materializes Body itself requires the eligible public
        // parent, matching URL parent and exact translations. No earlier CLR
        // decision is trusted to authorize this protected read.
        var parents = Courses(locale, db.Courses).Where(row => row.Translation.Slug == courseSlug
            && row.Course.ContentAccess == CourseContentAccess.Public);
        return (from course in parents
                join lesson in Lessons(db.Lessons) on course.Course.Id equals lesson.CourseId
                join translation in LessonTranslations(locale) on lesson.Id equals translation.LessonId
                where translation.Slug == lessonSlug
                select new PublicLessonReadRow(course.Course.Id, course.Translation.Slug, course.Translation.Title,
                    lesson.Id, translation.Slug, translation.Title, lesson.ContentTypeKey, lesson.ContentTypeSchemaVersion, translation.Body))
            .TagWith("P02d-4 lesson body").SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PublicCourseAlternate>> ReadCourseAlternatesAsync(
        CourseId parent, string locale, string[] enabled, CancellationToken cancellationToken) =>
        await (from course in EligibleCourses(db.Courses).Where(row => row.Id == parent)
               join translation in CourseTranslations() on course.Id equals translation.CourseId
               where translation.Locale != locale && enabled.Contains(translation.Locale)
               select new PublicCourseAlternate(translation.Locale, translation.Slug))
            .TagWith("P02d-4 course alternates").ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<PublicLessonAlternate>> ReadLessonAlternatesAsync(
        CourseId parent, LessonId lesson, string locale, string[] enabled, CancellationToken cancellationToken) =>
        await (from course in EligibleCourses(db.Courses).Where(row => row.Id == parent && row.ContentAccess == CourseContentAccess.Public)
               join courseTranslation in CourseTranslations() on course.Id equals courseTranslation.CourseId
               join child in Lessons(db.Lessons).Where(row => row.Id == lesson) on course.Id equals child.CourseId
               join translation in LessonTranslations(null) on new { child.Id, courseTranslation.Locale }
                   equals new { Id = translation.LessonId, translation.Locale }
               where courseTranslation.Locale != locale && enabled.Contains(courseTranslation.Locale)
               select new PublicLessonAlternate(courseTranslation.Locale, courseTranslation.Slug, translation.Slug))
            .TagWith("P02d-4 lesson alternates").ToArrayAsync(cancellationToken);

    private IQueryable<LocalizedCourse> Courses(string locale, IQueryable<Course> source) =>
        from course in EligibleCourses(source)
        join translation in CourseTranslations().Where(row => row.Locale == locale) on course.Id equals translation.CourseId
        select new LocalizedCourse { Course = course, Translation = translation };

    private IQueryable<Course> EligibleCourses(IQueryable<Course> source)
    {
        var host = Admit();
        return source.AsNoTracking().Where(row => row.TenantId == host.TenantId && row.DeletedAt == null
            && row.Status == PublicationStatus.Published
            && (row.OrganizationId == null || row.OrganizationId == host.OrganizationId));
    }
    private IQueryable<Lesson> Lessons(IQueryable<Lesson> source)
    {
        var host = Admit();
        return source.AsNoTracking().Where(row => row.TenantId == host.TenantId && row.DeletedAt == null
            && row.Status == PublicationStatus.Published
            && (row.OrganizationId == null || row.OrganizationId == host.OrganizationId));
    }
    private IQueryable<CourseTranslation> CourseTranslations()
    {
        var host = Admit();
        return db.Set<CourseTranslation>().AsNoTracking().Where(row => row.TenantId == host.TenantId
            && (row.OrganizationId == null || row.OrganizationId == host.OrganizationId));
    }
    private IQueryable<LessonTranslation> LessonTranslations(string? locale)
    {
        var host = Admit();
        var query = db.Set<LessonTranslation>().AsNoTracking().Where(row => row.TenantId == host.TenantId
            && (row.OrganizationId == null || row.OrganizationId == host.OrganizationId));
        return locale is null ? query : query.Where(row => row.Locale == locale);
    }
    private HostScope Admit()
    {
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.HostScope is not { } host
            || host.TenantId != context.TenantId || (host.OrganizationId is not null && host.OrganizationId != context.OrganizationId)
            || unit.Mode != TransactionMode.ReadOnly || !unit.HasActiveTransaction || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || db.Database.CurrentTransaction is not { } transaction || !ReferenceEquals(transaction.GetDbTransaction(), unit.Transaction))
            throw new TenantContextMissingException("Public Education reads require matching host provenance and an announced, enlisted read-only transaction.");
        return host;
    }

    private static readonly Expression<Func<LocalizedCourse, PublicCourseReadRow>> CourseRow = row => new(
        row.Course.Id, row.Course.CreatedAt, row.Translation.Slug, row.Translation.Title, row.Translation.Summary,
        row.Course.ContentAccess, row.Course.LevelTaxonomyKey, row.Course.LevelTaxonomySchemaVersion, row.Course.LevelBandKey);
    private sealed class LocalizedCourse
    {
        public required Course Course { get; init; }
        public required CourseTranslation Translation { get; init; }
    }
}

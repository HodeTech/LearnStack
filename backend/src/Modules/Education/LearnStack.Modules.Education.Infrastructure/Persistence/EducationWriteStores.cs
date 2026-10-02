using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Domain;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Education.Infrastructure.Persistence;

/// <summary>Own-module tracked course persistence on the announced ambient transaction.</summary>
public sealed class CourseWriteStore(EducationDbContext db) : ICourseWriteStore
{
    private static readonly HashSet<string> OwnedConstraints = new(StringComparer.Ordinal)
    {
        "pk_courses", "ux_courses_tenant_id_id", "ux_courses_tenant_id_slug_key",
        "pk_course_translations", "ux_course_translations_tenant_id_locale_slug",
    };
    public Task<Course?> FindAsync(CourseId id, CancellationToken cancellationToken) => db.Courses
        .Include(root => root.Translations).SingleOrDefaultAsync(root => root.Id == id && root.DeletedAt == null, cancellationToken);
    public Task AddAsync(Course aggregate, CancellationToken cancellationToken = default)
    {
        db.Courses.Add(aggregate);
        return WriteStoreTracking.SaveTranslatingConflictsAsync(db, cancellationToken, OwnedConstraints);
    }
    public Task UpdateAsync(Course aggregate, CancellationToken cancellationToken = default)
    {
        WriteStoreTracking.EnsureTracked(db, aggregate);
        return WriteStoreTracking.SaveTranslatingConflictsAsync(db, cancellationToken, OwnedConstraints);
    }
}

/// <summary>Own-module tracked lesson persistence; no parent aggregate is attached or saved.</summary>
public sealed class LessonWriteStore(EducationDbContext db) : ILessonWriteStore
{
    private static readonly HashSet<string> OwnedConstraints = new(StringComparer.Ordinal)
    {
        "pk_lessons", "ux_lessons_tenant_id_id", "pk_lesson_translations", "ux_lesson_translations_tenant_id_locale_slug",
    };
    public Task<Lesson?> FindAsync(LessonId id, CancellationToken cancellationToken) => db.Lessons
        .Include(root => root.Translations).SingleOrDefaultAsync(root => root.Id == id && root.DeletedAt == null, cancellationToken);
    public Task AddAsync(Lesson aggregate, CancellationToken cancellationToken = default)
    {
        db.Lessons.Add(aggregate);
        return WriteStoreTracking.SaveTranslatingConflictsAsync(db, cancellationToken, OwnedConstraints);
    }
    public Task UpdateAsync(Lesson aggregate, CancellationToken cancellationToken = default)
    {
        WriteStoreTracking.EnsureTracked(db, aggregate);
        return WriteStoreTracking.SaveTranslatingConflictsAsync(db, cancellationToken, OwnedConstraints);
    }
}

/// <summary>Filtered detached parent read on the same connection as the child's write.</summary>
public sealed class ParentCourseReader(EducationDbContext db) : IParentCourseReader
{
    public Task<Course?> ReadAsync(Guid courseId, CancellationToken cancellationToken)
    {
        var id = CourseId.From(courseId);
        return db.Courses.AsNoTracking().SingleOrDefaultAsync(root => root.Id == id && root.DeletedAt == null, cancellationToken);
    }
}

/// <summary>Own-module collision reads retain both parent and satellite scope filters.</summary>
public sealed class TranslationCollisionReader(EducationDbContext db) : ITranslationCollisionReader
{
    public async Task<Guid?> ReadCourseAsync(string locale, string slug, CancellationToken cancellationToken)
    {
        var id = await db.Courses.AsNoTracking().Where(root => root.DeletedAt == null
                && root.Translations.Any(translation => translation.Locale == locale && translation.Slug == slug))
            .Select(root => (CourseId?)root.Id).SingleOrDefaultAsync(cancellationToken);
        return id?.Value;
    }
    public async Task<Guid?> ReadLessonAsync(string locale, string slug, CancellationToken cancellationToken)
    {
        var id = await db.Lessons.AsNoTracking().Where(root => root.DeletedAt == null
                && root.Translations.Any(translation => translation.Locale == locale && translation.Slug == slug))
            .Select(root => (LessonId?)root.Id).SingleOrDefaultAsync(cancellationToken);
        return id?.Value;
    }
}

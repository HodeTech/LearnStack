using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Education.Application.Abstractions;

/// <summary>Filtered tracked course graphs; writes exactly this aggregate root.</summary>
public interface ICourseWriteStore : IAggregateWriteStore<Course, CourseId>
{
    Task<Course?> FindAsync(CourseId id, CancellationToken cancellationToken);
}

/// <summary>Filtered tracked lesson graphs; writes exactly this aggregate root.</summary>
public interface ILessonWriteStore : IAggregateWriteStore<Lesson, LessonId>
{
    Task<Lesson?> FindAsync(LessonId id, CancellationToken cancellationToken);
}

/// <summary>Read-only detached parent, used by the domain factory to derive child scope.</summary>
/// <remarks>No parent write port or tracked parent is exposed to the lesson handler.</remarks>
public interface IParentCourseReader
{
    Task<Course?> ReadAsync(CourseId courseId, CancellationToken cancellationToken);
}

/// <summary>Resolves only visible live root identities for bounded slug-collision diagnostics.</summary>
public interface ITranslationCollisionReader
{
    Task<CourseId?> ReadCourseAsync(string locale, string slug, CancellationToken cancellationToken);
    Task<LessonId?> ReadLessonAsync(string locale, string slug, CancellationToken cancellationToken);
}

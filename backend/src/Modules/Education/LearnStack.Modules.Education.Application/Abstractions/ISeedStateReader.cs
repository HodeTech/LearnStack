using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Modules.Education.Domain;

namespace LearnStack.Modules.Education.Application.Abstractions;

/// <summary>Filtered, uncached verification on the caller's announced transaction.</summary>
public interface ISeedStateReader
{
    Task<CourseSeedDto?> ReadCourseAsync(CourseId courseId, CancellationToken cancellationToken);
    Task<LessonSeedDto?> ReadLessonAsync(LessonId lessonId, CancellationToken cancellationToken);
}

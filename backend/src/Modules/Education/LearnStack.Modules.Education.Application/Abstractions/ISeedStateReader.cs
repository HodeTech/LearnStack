using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Education.Application.Abstractions;

/// <summary>Filtered, uncached verification on the caller's announced transaction.</summary>
public interface ISeedStateReader
{
    Task<CourseSeedDto?> ReadCourseAsync(Guid courseId, CancellationToken cancellationToken);
    Task<LessonSeedDto?> ReadLessonAsync(Guid lessonId, CancellationToken cancellationToken);
}

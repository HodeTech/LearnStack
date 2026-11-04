using LearnStack.Modules.Education.Domain;

namespace LearnStack.Modules.Education.Application.Abstractions;

/// <summary>Retained exact-scope history; parent-before-target row locks on the existing owner.</summary>
public interface ISeedEducationState
{
    Task PrepareAsync(Guid courseId, Guid? lessonId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Course>> ReadCoursesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Lesson>> ReadLessonsAsync(CancellationToken cancellationToken);
}

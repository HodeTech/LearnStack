using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Education.Application.Audit;

/// <summary>One-root writer classifications and trusted Off verification reads.</summary>
public sealed class EducationAuditCatalogSource : IAuditCatalogSource
{
    public string ModuleName => "education";

    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ShouldAudit<CreateCourseCommand>("education.course.create", OperationType.Create, typeof(Course));
        builder.ShouldAudit<AddCourseTranslationCommand>("education.course.translation_add", OperationType.Update, typeof(Course));
        builder.MustAudit<PublishCourseCommand>("education.course.publish", OperationType.Update, typeof(Course));
        builder.ShouldAudit<CreateLessonCommand>("education.lesson.create", OperationType.Create, typeof(Lesson));
        builder.ShouldAudit<AddLessonTranslationCommand>("education.lesson.translation_add", OperationType.Update, typeof(Lesson));
        builder.MustAudit<PublishLessonCommand>("education.lesson.publish", OperationType.Update, typeof(Lesson));
        builder.Off<GetCourseSeedStateQuery>();
        builder.Off<GetLessonSeedStateQuery>();
        builder.Off<GetPublicCoursesQuery>();
        builder.Off<GetPublicCourseQuery>();
        builder.Off<GetPublicLessonQuery>();
    }
}

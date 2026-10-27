using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Education.Application.Authorization;

public sealed class EducationOperationPolicySource : IOperationPolicySource
{
    public static PermissionDefinition PublicCourse { get; } = new(PermissionScope.Tenant, "education.public_course.read");
    public static PermissionDefinition PublicLesson { get; } = new(PermissionScope.Tenant, "education.public_lesson.read");
    public IEnumerable<PermissionRegistration> Definitions =>
        [new(PublicCourse, Assignable: false), new(PublicLesson, Assignable: false)];
    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForPublic<GetPublicCoursesQuery>("education.public_course.list", PublicCourse),
        OperationPolicy.ForPublic<GetPublicCourseQuery>("education.public_course.read", PublicCourse),
        OperationPolicy.ForPublic<GetPublicLessonQuery>("education.public_lesson.read", PublicLesson),
        OperationPolicy.ForTrusted<GetCourseSeedStateQuery>("education.seed.read_course", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetLessonSeedStateQuery>("education.seed.read_lesson", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<CreateCourseCommand>("education.seed.create_course", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<AddCourseTranslationCommand>("education.seed.add_course_translation", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<PublishCourseCommand>("education.seed.publish_course", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<CreateLessonCommand>("education.seed.create_lesson", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<AddLessonTranslationCommand>("education.seed.add_lesson_translation", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<PublishLessonCommand>("education.seed.publish_lesson", AuthorizationSourceKind.Seed),
    ];
}

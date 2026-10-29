using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Education.Application.Seeding;

public interface ISeedEducationWriteGuard
{
    Error Failure { get; }
    Task<bool> PrepareAsync(object request, CancellationToken cancellationToken);
    bool IsCurrent(object request);
}

/// <summary>Exact fixed advancement in ordinary organization scope, without a read hatch.</summary>
public sealed class SeedEducationWriteGuard(ISeedEducationContinuationSource source, ISeedEducationState state,
    ISeedTenantContinuationGuard tenancy, ISeedIdentityStageReader identity, ISeedCustomizationPrerequisiteReader customization,
    IIdentityMutationLocks locks, IOperationInvocationContext execution, ITenantContext context, IUnitOfWork unit)
    : ISeedEducationWriteGuard
{
    public Error Failure { get; private set; } = new(new LocalizedMessage("lockey_business_rule_violation"));

    private object? _request;
    private OperationInvocation? _invocation;
    private SeedEducationDeclaration? _declaration;
    private IdentityMutationLockPlan? _plan;
    private DbTransaction? _transaction;
    private long _generation;
    private bool _started;

    public async Task<bool> PrepareAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_started || !HasInvocation(execution.CurrentInvocation, request) || !HasOwner()
            || source.ForTenant(context.TenantId) is not { } declaration || declaration.TenantId != context.TenantId
            || Describe(request, declaration, context.OrganizationId) is not { } target) return false;
        _started = true;
        _request = request;
        _invocation = execution.CurrentInvocation;
        _declaration = declaration;
        _transaction = unit.Transaction;
        _generation = unit.GetTenantContextAnnouncement(_transaction)!.Generation;
        _plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Exclusive);
        await locks.AcquireAsync(_plan, cancellationToken);
        if (!IsCurrent(request)) return false;
        await state.PrepareAsync(target.Course.Id, target.Lesson?.Id, cancellationToken);
        if (!IsCurrent(request)) return false;
        var pristine = await tenancy.AllowsContentContinuationAsync(context.TenantId, requiresTheme: true, cancellationToken);
        if (!IsCurrent(request)) return false;
        if (!pristine) return ChangedState();
        var noIdentity = await identity.HasNoRoleHistoryAsync(_plan, cancellationToken);
        if (!IsCurrent(request)) return false;
        if (!noIdentity) return ChangedState();
        var completeCustomization = await customization.IsCompleteAsync(_plan, cancellationToken);
        if (!IsCurrent(request)) return false;
        if (!completeCustomization) return ChangedState();
        var courses = await state.ReadCoursesAsync(cancellationToken);
        if (!IsCurrent(request)) return false;
        var lessons = await state.ReadLessonsAsync(cancellationToken);
        if (!IsCurrent(request)) return false;
        return CanonicalPrefix(declaration, context.OrganizationId, courses, lessons, target)
            && AllowsStage(target, courses, lessons) || ChangedState();
    }

    private bool ChangedState()
    {
        Failure = new Error(new LocalizedMessage("lockey_concurrency_conflict"));
        return false;
    }

    public bool IsCurrent(object request)
    {
        if (!_started || !ReferenceEquals(_request, request) || !ReferenceEquals(_invocation, execution.CurrentInvocation)
            || !HasInvocation(_invocation, request) || !HasOwner() || !ReferenceEquals(_transaction, unit.Transaction)
            || unit.GetTenantContextAnnouncement(_transaction)?.Generation != _generation
            || !ReferenceEquals(source.ForTenant(context.TenantId), _declaration)
            || Describe(request, _declaration!, context.OrganizationId) is null)
        {
            if (unit.HasActiveTransaction) unit.MarkRollbackOnly();
            return false;
        }
        locks.RequireCurrent(_plan!);
        return true;
    }

    private bool HasOwner() => context.IsResolved && context.HostScope is null && unit.HasActiveTransaction && !unit.IsRollbackOnly
        && unit.Mode == TransactionMode.ReadWrite && unit is not IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
        && unit.GetTenantContextAnnouncement(unit.Transaction) is { Generation: > 0 } announcement
        && announcement.TenantId == context.TenantId && announcement.OrganizationId == context.OrganizationId
        && unit is not IOperationReadScopeView { IsTenantWideRead: true };

    private bool HasInvocation(OperationInvocation? invocation, object request) => invocation is
    { IsActive: true, EntryKind: OperationEntryKind.Application }
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && invocation.Policy.RequestType == request.GetType() && invocation.Policy.Kind == OperationPolicyKind.FiniteTrusted
        && invocation.Policy.AllowedSources.Length == 1 && invocation.Policy.AllowedSources[0] == AuthorizationSourceKind.Seed
        && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed } trusted && trusted.Matches(request.GetType(), context)
        && invocation.Policy.Id == Policy(request);

    private static string? Policy(object request) => request switch
    {
        CreateCourseCommand => "education.seed.create_course",
        AddCourseTranslationCommand => "education.seed.add_course_translation",
        PublishCourseCommand => "education.seed.publish_course",
        CreateLessonCommand => "education.seed.create_lesson",
        AddLessonTranslationCommand => "education.seed.add_lesson_translation",
        PublishLessonCommand => "education.seed.publish_lesson",
        _ => null,
    };

    private static Target? Describe(object request, SeedEducationDeclaration declaration, OrganizationId? organization)
    {
        foreach (var course in declaration.Courses.Where(value => value.OrganizationId == organization))
        {
            if (request is CreateCourseCommand create && create.CourseId == course.Id && create.SlugKey == course.SlugKey
                && create.ContentAccess == course.ContentAccess && create.LevelTaxonomyKey == course.LevelTaxonomyKey
                && create.LevelTaxonomySchemaVersion == course.LevelTaxonomySchemaVersion && create.LevelBandKey == course.LevelBandKey)
                return new(course, null, Kind.CreateCourse);
            if (request is PublishCourseCommand publish && publish.CourseId == course.Id && course.Status == "Published"
                && publish.ExpectedVersion == course.Translations.Length) return new(course, null, Kind.PublishCourse);
            for (var index = 0; index < course.Translations.Length; index++)
            {
                var translation = course.Translations[index];
                if (request is AddCourseTranslationCommand add && add.CourseId == course.Id && add.ExpectedVersion == index
                    && add.Locale == translation.Locale && add.Title == translation.Title && add.Summary == translation.Summary
                    && add.Slug == translation.Slug) return new(course, null, Kind.CourseTranslation, index);
            }
            foreach (var lesson in course.Lessons)
            {
                if (request is CreateLessonCommand createLesson && createLesson.LessonId == lesson.Id && createLesson.CourseId == course.Id
                    && createLesson.Sort == lesson.Sort && createLesson.ContentTypeKey == lesson.ContentTypeKey
                    && createLesson.ContentTypeSchemaVersion == lesson.ContentTypeSchemaVersion) return new(course, lesson, Kind.CreateLesson);
                if (request is PublishLessonCommand publishLesson && publishLesson.LessonId == lesson.Id && lesson.Status == "Published"
                    && publishLesson.ExpectedVersion == lesson.Translations.Length) return new(course, lesson, Kind.PublishLesson);
                for (var index = 0; index < lesson.Translations.Length; index++)
                {
                    var translation = lesson.Translations[index];
                    if (request is AddLessonTranslationCommand addLesson && addLesson.LessonId == lesson.Id && addLesson.ExpectedVersion == index
                        && addLesson.Locale == translation.Locale && addLesson.Title == translation.Title && addLesson.Slug == translation.Slug
                        && addLesson.Body == translation.Body) return new(course, lesson, Kind.LessonTranslation, index);
                }
            }
        }
        return null;
    }

    private static bool CanonicalPrefix(SeedEducationDeclaration declaration, OrganizationId? organization,
        IReadOnlyList<Course> courses, IReadOnlyList<Lesson> lessons, Target target)
    {
        var declared = declaration.Courses.Where(course => course.OrganizationId == organization).ToArray();
        if (courses.Any(row => !declared.Any(course => course.Id == row.Id.Value))
            || lessons.Any(row => !declared.Any(course => course.Id == row.CourseId.Value
                && course.Lessons.Any(lesson => lesson.Id == row.Id.Value)))) return false;
        var predecessorComplete = true;
        foreach (var expected in declared)
        {
            var row = courses.SingleOrDefault(course => course.Id.Value == expected.Id);
            var children = lessons.Where(lesson => lesson.CourseId.Value == expected.Id).ToArray();
            if (row is null)
            {
                // Validate the proposed creation as well as the retained graph. A
                // canonical later root must not turn a valid prefix into a gap.
                if (children.Length != 0 || target.Operation == Kind.CreateCourse
                    && target.Course.Id == expected.Id && !predecessorComplete) return false;
                predecessorComplete = false;
                continue;
            }
            if (!predecessorComplete || !CourseMatches(row, expected, declaration.TenantId)) return false;
            var translationsComplete = row.Translations.Count == expected.Translations.Length;
            if (!translationsComplete && children.Length != 0) return false;
            var previousLessonComplete = true;
            foreach (var expectedLesson in expected.Lessons)
            {
                var lesson = children.SingleOrDefault(value => value.Id.Value == expectedLesson.Id);
                if (lesson is null)
                {
                    if (target.Operation == Kind.CreateLesson && target.Lesson!.Id == expectedLesson.Id
                        && !previousLessonComplete) return false;
                    previousLessonComplete = false;
                    continue;
                }
                if (!previousLessonComplete || !LessonMatches(lesson, expected, expectedLesson, declaration.TenantId)) return false;
                previousLessonComplete = lesson.Translations.Count == expectedLesson.Translations.Length;
            }
            predecessorComplete = translationsComplete && previousLessonComplete && children.Length == expected.Lessons.Length;
            if (row.Status == PublicationStatus.Published && (!predecessorComplete
                || expected.Lessons.Any(expectedLesson => children.Single(child => child.Id.Value == expectedLesson.Id).Status.ToString() != expectedLesson.Status)))
                return false;
        }
        return true;
    }

    private static bool AllowsStage(Target target, IReadOnlyList<Course> courses, IReadOnlyList<Lesson> lessons)
    {
        var course = courses.SingleOrDefault(value => value.Id.Value == target.Course.Id);
        var lesson = target.Lesson is { } expected ? lessons.SingleOrDefault(value => value.Id.Value == expected.Id) : null;
        return target.Operation switch
        {
            Kind.CreateCourse => course is null && !courses.Any(value => value.SlugKey == target.Course.SlugKey),
            Kind.CourseTranslation => course is { Status: PublicationStatus.Draft } && course.Version == target.TranslationIndex,
            Kind.CreateLesson => ParentComplete(course, target.Course) && lesson is null
                && !lessons.Any(value => value.CourseId.Value == target.Course.Id && value.Sort == target.Lesson!.Sort),
            Kind.LessonTranslation => ParentComplete(course, target.Course) && lesson is { Status: PublicationStatus.Draft }
                && lesson.Version == target.TranslationIndex,
            Kind.PublishLesson => ParentComplete(course, target.Course) && lesson is { Status: PublicationStatus.Draft }
                && lesson.Version == target.Lesson!.Translations.Length,
            Kind.PublishCourse => ParentComplete(course, target.Course) && target.Course.Lessons.All(expectedLesson =>
                lessons.Any(value => value.Id.Value == expectedLesson.Id && value.Translations.Count == expectedLesson.Translations.Length
                    && value.Status.ToString() == expectedLesson.Status)),
            _ => false,
        };
    }

    private static bool ParentComplete(Course? row, SeedCourseDeclaration expected) => row is { Status: PublicationStatus.Draft }
        && row.Version == expected.Translations.Length;

    private static bool CourseMatches(Course row, SeedCourseDeclaration expected, TenantId tenant)
    {
        var published = row.Status == PublicationStatus.Published;
        return row.TenantId == tenant && row.OrganizationId == expected.OrganizationId && row.SlugKey == expected.SlugKey
            && (row.ContentAccess == CourseContentAccess.Public ? "public" : "enrollment_required") == expected.ContentAccess
            && row.LevelTaxonomyKey == expected.LevelTaxonomyKey && row.LevelTaxonomySchemaVersion == expected.LevelTaxonomySchemaVersion
            && row.LevelBandKey == expected.LevelBandKey && row.Translations.Count <= expected.Translations.Length
            && (row.Status == PublicationStatus.Draft || published && expected.Status == "Published" && row.Translations.Count == expected.Translations.Length)
            && Pristine(row, row.Translations.Count + (published ? 1 : 0))
            && row.Translations.All(actual => actual.TenantId == tenant && actual.OrganizationId == expected.OrganizationId
                && actual.CourseId == row.Id && expected.Translations.Take(row.Translations.Count).Any(value => value.Locale == actual.Locale
                    && value.Title == actual.Title && value.Summary == actual.Summary && value.Slug == actual.Slug));
    }

    private static bool LessonMatches(Lesson row, SeedCourseDeclaration parent, SeedLessonDeclaration expected, TenantId tenant)
    {
        var published = row.Status == PublicationStatus.Published;
        return row.TenantId == tenant && row.OrganizationId == parent.OrganizationId && row.CourseId.Value == parent.Id
            && row.Sort == expected.Sort && row.ContentTypeKey == expected.ContentTypeKey && row.ContentTypeSchemaVersion == expected.ContentTypeSchemaVersion
            && row.Translations.Count <= expected.Translations.Length
            && (row.Status == PublicationStatus.Draft || published && expected.Status == "Published" && row.Translations.Count == expected.Translations.Length)
            && Pristine(row, row.Translations.Count + (published ? 1 : 0))
            && row.Translations.All(actual => actual.TenantId == tenant && actual.OrganizationId == parent.OrganizationId
                && actual.LessonId == row.Id && expected.Translations.Take(row.Translations.Count).Any(value => value.Locale == actual.Locale
                    && value.Title == actual.Title && value.Slug == actual.Slug && Json(value.Body, actual.Body)));
    }

    private static bool Pristine<TId>(AuditableEntity<TId> row, long version)
        where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId> => row.Version == version
        && row.CreatedAt != default && row.CreatedBy == UserId.SystemActor && row.DeletedAt is null && row.DeletedBy is null
        && (version == 0 ? row.UpdatedAt is null && row.UpdatedBy is null : row.UpdatedAt is { } at && at != default && row.UpdatedBy == UserId.SystemActor);

    private static bool Json(string left, string right)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return false; }
    }

    private enum Kind { CreateCourse, CourseTranslation, PublishCourse, CreateLesson, LessonTranslation, PublishLesson }
    private sealed record Target(SeedCourseDeclaration Course, SeedLessonDeclaration? Lesson, Kind Operation, int TranslationIndex = 0);
}

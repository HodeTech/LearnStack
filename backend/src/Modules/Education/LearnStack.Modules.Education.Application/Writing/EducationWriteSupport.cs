using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Education.Application.Writing;

internal static class EducationWriteSupport
{
    internal static Result<T>? Context<T>(ITenantContext context) => context.IsResolved
        ? null : Code<T>("lockey_tenant_mismatch");

    internal static Result<T>? Scope<T>(IOrganizationScoped root, ITenantContext context) =>
        root.TenantId != context.TenantId ? Code<T>("lockey_not_found")
            : root.OrganizationId != context.OrganizationId ? Code<T>("lockey_resource_scope_violation") : null;

    internal static Result<T>? Version<T>(long version, long? expected) => version == expected
        ? null : Code<T>("lockey_concurrency_conflict");

    internal static Result<T>? Draft<T>(PublicationStatus status) => status == PublicationStatus.Draft
        ? null : Result<T>.Fail(FieldError("lockey_business_rule_violation", "Status", "lockey_education_translation_requires_draft"));

    internal static Result<T> Code<T>(string code) => Result<T>.Fail(new Error(new LocalizedMessage(code)));
    internal static Result<T> Field<T>(string field, string reason) => Result<T>.Fail(FieldError("lockey_validation_failed", field, reason));

    private static Error FieldError(string code, string field, string reason) => new(new LocalizedMessage(code),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            [field] = [new LocalizedMessage(reason)],
        });

    internal static CourseWriteDto Dto(Course root) => new(root.Id.Value, root.Version, root.Status.ToString(),
        root.ContentAccess == CourseContentAccess.Public ? "public" : "enrollment_required");
    internal static LessonWriteDto Dto(Lesson root) => new(root.Id.Value, root.Version, root.Status.ToString());

    internal static async Task<Result<T>> SaveAsync<T>(Func<Task> save, Func<T> response, IUnitOfWork unit, Func<string?, Task<Error?>>? explainConflict = null)
    {
        try
        {
            await save();
        }
        catch (AggregateConcurrencyException)
        {
            unit.MarkRollbackOnly();
            return Code<T>("lockey_concurrency_conflict");
        }
        catch (AggregateConflictException conflict) when (KnownConflict(conflict.ConstraintName) is not null)
        {
            unit.MarkRollbackOnly();
            if (explainConflict is not null && await explainConflict(conflict.ConstraintName) is { } explanation)
                return Result<T>.Fail(explanation);
            // The exception filter admitted only a constraint with a known mapping.
            var (field, reason) = KnownConflict(conflict.ConstraintName)!.Value;
            return Result<T>.Fail(FieldError("lockey_business_rule_violation", field, reason));
        }

        return Result.Ok(response());
    }

    internal static Error SlugConflict(string locale, string slug, Guid? visibleRoot)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["locale"] = locale, ["slug"] = slug };
        if (visibleRoot is { } id) parameters["entityId"] = id.ToString();
        return new Error(new LocalizedMessage("lockey_business_rule_violation"),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
            {
                ["Slug"] = [new LocalizedMessage("lockey_slug_taken", parameters)],
            });
    }

    private static (string Field, string Reason)? KnownConflict(string? constraint) => constraint switch
    {
        "pk_courses" or "ux_courses_tenant_id_id" or "pk_lessons" or "ux_lessons_tenant_id_id" =>
            ("Id", "lockey_identifier_taken"),
        "ux_courses_tenant_id_slug_key" => ("SlugKey", "lockey_slug_taken"),
        "pk_course_translations" or "pk_lesson_translations" => ("Locale", "lockey_education_locale_already_exists"),
        "ux_course_translations_tenant_id_locale_slug" or "ux_lesson_translations_tenant_id_locale_slug" =>
            ("Slug", "lockey_slug_taken"),
        _ => null,
    };
}

using System.Text.Json;
using FluentValidation;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Validation;

namespace LearnStack.Modules.Education.Application.Writing;

internal sealed class CreateCourseCommandValidator : AbstractValidator<CreateCourseCommand>
{
    public CreateCourseCommandValidator()
    {
        RuleFor(request => request.CourseId).NotEmpty().WithErrorCode("lockey_identifier_invalid");
        RuleFor(request => request.SlugKey).Must(EducationSlug.IsValid).WithErrorCode("lockey_education_slug_invalid");
        RuleFor(request => request.ContentAccess).Must(value => value is "public" or "enrollment_required")
            .WithErrorCode("lockey_education_content_access_invalid");
        RuleFor(request => request).Must(request =>
                request.LevelTaxonomyKey is null && request.LevelTaxonomySchemaVersion is null && request.LevelBandKey is null
                || request is { LevelTaxonomyKey: { } key, LevelTaxonomySchemaVersion: > 0, LevelBandKey: { } band }
                    && EducationPinKey.IsValid(key) && EducationPinKey.IsValid(band))
            .OverridePropertyName(nameof(CreateCourseCommand.LevelTaxonomyKey)).WithErrorCode("lockey_education_level_pin_invalid");
    }
}

internal sealed class CreateLessonCommandValidator : AbstractValidator<CreateLessonCommand>
{
    public CreateLessonCommandValidator()
    {
        RuleFor(request => request.LessonId).NotEmpty().WithErrorCode("lockey_identifier_invalid");
        RuleFor(request => request.CourseId).NotEmpty().WithErrorCode("lockey_identifier_invalid");
        RuleFor(request => request.Sort).GreaterThanOrEqualTo(0).WithErrorCode("lockey_education_sort_invalid");
        RuleFor(request => request.ContentTypeKey).Must(EducationPinKey.IsValid).WithErrorCode("lockey_education_content_type_invalid");
        RuleFor(request => request.ContentTypeSchemaVersion).GreaterThan(0).WithErrorCode("lockey_education_content_type_invalid");
    }
}

internal sealed class AddCourseTranslationCommandValidator : AbstractValidator<AddCourseTranslationCommand>
{
    public AddCourseTranslationCommandValidator()
    {
        RuleFor(request => request.CourseId).NotEmpty().WithErrorCode("lockey_identifier_invalid");
        RuleFor(request => request.ExpectedVersion).Must(value => value is >= 0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Locale).Must(EducationInput.Locale).WithErrorCode("lockey_education_locale_invalid");
        RuleFor(request => request.Title).Must(EducationInput.Title).WithErrorCode("lockey_education_title_invalid");
        RuleFor(request => request.Summary).Must(value => value is null || JsonValue.IsStorableText(value)).WithErrorCode("lockey_education_summary_invalid");
        RuleFor(request => request.Slug).Must(EducationSlug.IsValid).WithErrorCode("lockey_education_slug_invalid");
    }
}

internal sealed class AddLessonTranslationCommandValidator : AbstractValidator<AddLessonTranslationCommand>
{
    public AddLessonTranslationCommandValidator()
    {
        RuleFor(request => request.LessonId).NotEmpty().WithErrorCode("lockey_identifier_invalid");
        RuleFor(request => request.ExpectedVersion).Must(value => value is >= 0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Locale).Must(EducationInput.Locale).WithErrorCode("lockey_education_locale_invalid");
        RuleFor(request => request.Title).Must(EducationInput.Title).WithErrorCode("lockey_education_title_invalid");
        RuleFor(request => request.Slug).Must(EducationSlug.IsValid).WithErrorCode("lockey_education_slug_invalid");
        RuleFor(request => request.Body).Must(EducationInput.Body).WithErrorCode("lockey_education_body_invalid");
    }
}

internal sealed class PublishCourseCommandValidator : AbstractValidator<PublishCourseCommand>
{
    public PublishCourseCommandValidator()
    {
        RuleFor(request => request.CourseId).NotEmpty().WithErrorCode("lockey_identifier_invalid");
        RuleFor(request => request.ExpectedVersion).Must(value => value is >= 0).WithErrorCode("lockey_concurrency_conflict");
    }
}

internal sealed class PublishLessonCommandValidator : AbstractValidator<PublishLessonCommand>
{
    public PublishLessonCommandValidator()
    {
        RuleFor(request => request.LessonId).NotEmpty().WithErrorCode("lockey_identifier_invalid");
        RuleFor(request => request.ExpectedVersion).Must(value => value is >= 0).WithErrorCode("lockey_concurrency_conflict");
    }
}

internal static class EducationInput
{
    internal static bool Title(string value) => !string.IsNullOrWhiteSpace(value) && JsonValue.IsStorableText(value);
    internal static bool Body(string value)
    {
        if (!JsonInstanceLimits.IsWithinCap(value) || !JsonValue.IsWellFormed(value)) return false;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.ValueKind == JsonValueKind.Object;
    }
    internal static bool Locale(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > LocaleTag.MaxLength) return false;
        try { LocaleTag.EnsureWellFormed(value, nameof(value)); return true; }
        catch (ArgumentException) { return false; }
    }
}

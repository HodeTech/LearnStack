using FluentValidation;
using FluentValidation.Results;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Education.Application.PublicReads;

public static class PublicReadValidation
{
    public static bool TryLocale(string? raw, out string locale)
    {
        locale = "";
        if (string.IsNullOrEmpty(raw) || raw.Length > LocaleTag.MaxLength) return false;
        try
        {
            LocaleTag.EnsureWellFormed(raw, nameof(raw));
            locale = LocaleTag.Canonicalize(raw);
            return true;
        }
        catch (ArgumentException) { return false; }
    }
    public static bool TryLimit(string? raw, out int limit)
    {
        limit = 20;
        if (raw is null) return true;
        limit = 0;
        if (raw.Length == 0) return false;
        foreach (var character in raw)
        {
            if (!char.IsAsciiDigit(character)) return false;
            limit = Math.Min(100, limit * 10 + character - '0');
        }
        return limit > 0;
    }
    public static string? Scope(ITenantContext context, PublicReadInput input, string locale, bool outline) =>
        context.HostScope is { } host ? PublicCursorCodec.Scope(host.TenantId, host.OrganizationId, input.HostDigest, locale, outline) : null;

    public static void Validate<T>(PublicReadInput? input, ITenantContext tenantContext, bool outline, ValidationContext<T> context)
    {
        void Invalid(string field) => context.AddFailure(new ValidationFailure(field, "lockey_invalid_value") { ErrorCode = "lockey_invalid_value" });
        if (input is null) { Invalid("query"); return; }
        foreach (var field in input.InvalidFields.Distinct(StringComparer.Ordinal).Take(5))
            Invalid(field is "locale" or "cursor" or "limit" or "lessonCursor" or "lessonLimit" ? field : "query");
        var localeValid = TryLocale(input.Locale, out var locale);
        if (!localeValid) Invalid("locale");
        if (!TryLimit(input.Limit, out _)) Invalid(outline ? "lessonLimit" : "limit");
        if (input.Cursor is { } raw)
        {
            var scope = localeValid ? Scope(tenantContext, input, locale, outline) : null;
            if (outline ? !PublicCursorCodec.TryOutline(raw, scope, out _) : !PublicCursorCodec.TryCatalog(raw, scope, out _))
                Invalid(outline ? "lessonCursor" : "cursor");
        }
    }
}

public sealed class GetPublicCoursesQueryValidator : AbstractValidator<GetPublicCoursesQuery>
{
    public GetPublicCoursesQueryValidator(ITenantContext context) => RuleFor(request => request.Input)
        .Custom((input, validation) => PublicReadValidation.Validate(input, context, false, validation));
}
public sealed class GetPublicCourseQueryValidator : AbstractValidator<GetPublicCourseQuery>
{
    public GetPublicCourseQueryValidator(ITenantContext context)
    {
        RuleFor(request => request.Slug).Must(EducationSlug.IsValid).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Input).Custom((input, validation) => PublicReadValidation.Validate(input, context, true, validation));
    }
}
public sealed class GetPublicLessonQueryValidator : AbstractValidator<GetPublicLessonQuery>
{
    public GetPublicLessonQueryValidator(ITenantContext context)
    {
        RuleFor(request => request.Slug).Must(EducationSlug.IsValid).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.LessonSlug).Must(EducationSlug.IsValid).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Input).Custom((input, validation) => PublicReadValidation.Validate(input, context, false, validation));
    }
}

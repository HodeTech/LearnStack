using System.Text.Json.Serialization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Education.Application.Contracts.PublicReads;

/// <summary>Raw bounded transport values; no tenant or organization selector.</summary>
public sealed record PublicReadInput(
    string? Locale, string? Cursor, string? Limit, string HostDigest, IReadOnlyList<string> InvalidFields);

[PublicSurface]
public sealed record GetPublicCoursesQuery(PublicReadInput Input) : IRequest<Result<PublicCourseCatalog>>;
[PublicSurface]
public sealed record GetPublicCourseQuery(string Slug, PublicReadInput Input) : IRequest<Result<PublicCourseDetail>>;
[PublicSurface]
public sealed record GetPublicLessonQuery(string Slug, string LessonSlug, PublicReadInput Input) : IRequest<Result<PublicLessonDetail>>;

[JsonConverter(typeof(JsonStringEnumConverter<PublicCourseAccess>))]
public enum PublicCourseAccess
{
    [JsonStringEnumMemberName("public")] Public,
    [JsonStringEnumMemberName("enrollment_required")] EnrollmentRequired,
}
[JsonConverter(typeof(JsonStringEnumConverter<PublicDisplayState>))]
public enum PublicDisplayState
{
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
}

public sealed record PublicLabel(string Value, string Locale);
public sealed record PublicLevel(PublicDisplayState State, PublicLabel? Label);
public sealed record PublicCourseSummary(
    string Slug, string Title, string? Summary, PublicCourseAccess ContentAccess, PublicLevel? Level);
public sealed record PublicCourseCatalog(string Locale, IReadOnlyList<PublicCourseSummary> Items, PageInfo PageInfo);
public sealed record PublicCourseAlternate(string Locale, string Slug);
public sealed record PublicOutlineItem(string Slug, string Title, int Sort);
public sealed record PublicCourseOutline(IReadOnlyList<PublicOutlineItem> Items, PageInfo PageInfo);
public sealed record PublicCourseDetail(
    string Locale, PublicCourseSummary Course, IReadOnlyList<PublicCourseAlternate> Alternates, PublicCourseOutline? Lessons);
public sealed record PublicNamedResource(string Slug, string Title);
public sealed record PublicLessonAlternate(string Locale, string CourseSlug, string LessonSlug);
public sealed record PublicTextField(string Name, PublicLabel Label, string Value);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "state")]
[JsonDerivedType(typeof(PublicReadyContent), "ready")]
[JsonDerivedType(typeof(PublicUnavailableContent), "unavailable")]
public abstract record PublicLessonContent;
public sealed record PublicReadyContent(string RendererKey, PublicLabel Label, IReadOnlyList<PublicTextField> Fields) : PublicLessonContent;
public sealed record PublicUnavailableContent : PublicLessonContent;
public sealed record PublicLessonDetail(
    string Locale, PublicNamedResource Course, PublicNamedResource Lesson,
    IReadOnlyList<PublicLessonAlternate> Alternates, PublicLessonContent Content);

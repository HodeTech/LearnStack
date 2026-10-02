using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Education.Application.Contracts.Lessons;

/// <summary>One lesson's identity, committed root version and independent publication state.</summary>
public sealed record LessonWriteDto(Guid Id, long Version, string Status);

/// <summary>Creates a draft from a visible writable parent and an exact Active content-type pin.</summary>
/// <remarks>Scope is derived from the parent and trusted context; the command carries no tenant authority.</remarks>
public sealed record CreateLessonCommand(Guid LessonId, Guid CourseId, int Sort,
    string ContentTypeKey, int ContentTypeSchemaVersion) : IRequest<Result<LessonWriteDto>>;

/// <summary>Adds draft translated content validated against the lesson's immutable Active/Deprecated pin.</summary>
/// <param name="LessonId">Explicit visible root identity.</param>
/// <param name="ExpectedVersion">Required exact root version; null is refused.</param>
/// <param name="Locale">Tenant-enabled canonicalizable locale.</param>
/// <param name="Title">Nonblank storable title.</param>
/// <param name="Slug">Routable slug reserved on insertion.</param>
/// <param name="Body">A JSON object validated against the exact pinned admitted schema.</param>
public sealed record AddLessonTranslationCommand(Guid LessonId, long? ExpectedVersion,
    string Locale, string Title, string Slug, string Body) : IRequest<Result<LessonWriteDto>>;

/// <summary>Publishes one draft lesson at its exact version; its course remains unchanged.</summary>
public sealed record PublishLessonCommand(Guid LessonId, long? ExpectedVersion) : IRequest<Result<LessonWriteDto>>;

using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Education.Application.Contracts.Courses;

/// <summary>One course's identity, committed root version, publication and content policy.</summary>
public sealed record CourseWriteDto(Guid Id, long Version, string Status, string ContentAccess);

/// <summary>Creates a draft in trusted context scope; policy is explicitly public or enrollment_required.</summary>
/// <remarks>The optional taxonomy reference is an all-or-none exact Active revision/band pin.</remarks>
public sealed record CreateCourseCommand(Guid CourseId, string SlugKey, string? ContentAccess,
    string? LevelTaxonomyKey = null, int? LevelTaxonomySchemaVersion = null, string? LevelBandKey = null)
    : IRequest<Result<CourseWriteDto>>;

/// <summary>Adds one enabled canonical locale's draft translation; never overwrites an existing locale.</summary>
/// <param name="CourseId">Explicit root identity, visible and writable in trusted context.</param>
/// <param name="ExpectedVersion">Required exact root version; null is refused.</param>
/// <param name="Locale">Tenant-enabled canonicalizable locale.</param>
/// <param name="Title">Nonblank storable title.</param>
/// <param name="Summary">Optional storable marketing summary.</param>
/// <param name="Slug">Routable slug reserved on insertion, independently of publication.</param>
public sealed record AddCourseTranslationCommand(Guid CourseId, long? ExpectedVersion,
    string Locale, string Title, string? Summary, string Slug) : IRequest<Result<CourseWriteDto>>;

/// <summary>Publishes one draft course at its exact version; no lesson or access grant changes.</summary>
public sealed record PublishCourseCommand(Guid CourseId, long? ExpectedVersion) : IRequest<Result<CourseWriteDto>>;

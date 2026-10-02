using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Writing;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;

using LearnStack.Modules.Education.Application.Contracts.Courses;

namespace LearnStack.Modules.Education.Application.Courses;

internal sealed class AddCourseTranslationCommandHandler(ICourseWriteStore roots, ITenantLocaleEligibilityReader locales, ITranslationCollisionReader collisions, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock)
    : IRequestHandler<AddCourseTranslationCommand, Result<CourseWriteDto>>
{
    public async Task<Result<CourseWriteDto>> Handle(AddCourseTranslationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EducationWriteSupport.Context<CourseWriteDto>(tenantContext) is { } unresolved) return unresolved;
        var root = await roots.FindAsync(CourseId.From(request.CourseId), cancellationToken);
        if (root is null) return EducationWriteSupport.Code<CourseWriteDto>("lockey_not_found");
        if (EducationWriteSupport.Scope<CourseWriteDto>(root, tenantContext) is { } scopeFailure) return scopeFailure;
        auditSubject.Designate(root);
        if (EducationWriteSupport.Version<CourseWriteDto>(root.Version, request.ExpectedVersion) is { } stale) return stale;
        if (EducationWriteSupport.Draft<CourseWriteDto>(root.Status) is { } lifecycle) return lifecycle;
        var locale = await locales.ReadEligibleAsync(request.Locale, cancellationToken);
        if (locale.IsFailure) return Result<CourseWriteDto>.Fail(locale.Error);

        var added = root.AddTranslation(locale.Value, request.Title, request.Summary, request.Slug, clock, tenantContext.UserId ?? UserId.SystemActor);
        if (added.IsFailure) return Result<CourseWriteDto>.Fail(added.Error);
        return await EducationWriteSupport.SaveAsync(() => roots.UpdateAsync(root, cancellationToken),
            () => EducationWriteSupport.Dto(root), unitOfWork, async constraint =>
            {
                if (constraint != "ux_course_translations_tenant_id_locale_slug") return null;
                var visible = await collisions.ReadCourseAsync(locale.Value, request.Slug, cancellationToken);
                return EducationWriteSupport.SlugConflict(locale.Value, request.Slug, visible?.Value);
            });
    }
}

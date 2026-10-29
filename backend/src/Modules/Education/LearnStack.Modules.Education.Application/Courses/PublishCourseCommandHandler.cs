using LearnStack.Modules.Education.Application.Seeding;
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

using LearnStack.Modules.Education.Application.Contracts.Courses;

namespace LearnStack.Modules.Education.Application.Courses;

internal sealed class PublishCourseCommandHandler(ICourseWriteStore roots, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock, ISeedEducationWriteGuard seedGuard)
    : IRequestHandler<PublishCourseCommand, Result<CourseWriteDto>>
{
    public async Task<Result<CourseWriteDto>> Handle(PublishCourseCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EducationWriteSupport.Context<CourseWriteDto>(tenantContext) is { } unresolved) return unresolved;
        if (!await seedGuard.PrepareAsync(request, cancellationToken)) return Result<CourseWriteDto>.Fail(seedGuard.Failure);
        var root = await roots.FindAsync(CourseId.From(request.CourseId), cancellationToken);
        if (root is null) return EducationWriteSupport.Code<CourseWriteDto>("lockey_not_found");
        if (EducationWriteSupport.Scope<CourseWriteDto>(root, tenantContext) is { } scopeFailure) return scopeFailure;
        auditSubject.Designate(root);
        if (EducationWriteSupport.Version<CourseWriteDto>(root.Version, request.ExpectedVersion) is { } stale) return stale;
        if (!seedGuard.IsCurrent(request)) return EducationWriteSupport.Code<CourseWriteDto>("lockey_business_rule_violation");
        var published = root.Publish(clock, tenantContext.UserId ?? UserId.SystemActor);
        if (published.IsFailure) return Result<CourseWriteDto>.Fail(published.Error);
        return await EducationWriteSupport.SaveAsync(() => roots.UpdateAsync(root, cancellationToken),
            () => EducationWriteSupport.Dto(root), unitOfWork);
    }
}

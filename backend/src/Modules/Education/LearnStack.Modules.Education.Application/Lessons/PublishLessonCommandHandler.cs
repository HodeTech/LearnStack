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

using LearnStack.Modules.Education.Application.Contracts.Lessons;

namespace LearnStack.Modules.Education.Application.Lessons;

internal sealed class PublishLessonCommandHandler(ILessonWriteStore roots, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock)
    : IRequestHandler<PublishLessonCommand, Result<LessonWriteDto>>
{
    public async Task<Result<LessonWriteDto>> Handle(PublishLessonCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EducationWriteSupport.Context<LessonWriteDto>(tenantContext) is { } unresolved) return unresolved;
        var root = await roots.FindAsync(LessonId.From(request.LessonId), cancellationToken);
        if (root is null) return EducationWriteSupport.Code<LessonWriteDto>("lockey_not_found");
        if (EducationWriteSupport.Scope<LessonWriteDto>(root, tenantContext) is { } scopeFailure) return scopeFailure;
        auditSubject.Designate(root);
        if (EducationWriteSupport.Version<LessonWriteDto>(root.Version, request.ExpectedVersion) is { } stale) return stale;
        var published = root.Publish(clock, tenantContext.UserId ?? UserId.SystemActor);
        if (published.IsFailure) return Result<LessonWriteDto>.Fail(published.Error);
        return await EducationWriteSupport.SaveAsync(() => roots.UpdateAsync(root, cancellationToken),
            () => EducationWriteSupport.Dto(root), unitOfWork);
    }
}

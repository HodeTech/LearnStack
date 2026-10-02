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

using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Education.Application.Contracts.Lessons;

namespace LearnStack.Modules.Education.Application.Lessons;

internal sealed class CreateLessonCommandHandler(ILessonWriteStore lessons, IParentCourseReader parents,
    IExactCustomizationDefinitionReader definitions, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock)
    : IRequestHandler<CreateLessonCommand, Result<LessonWriteDto>>
{
    public async Task<Result<LessonWriteDto>> Handle(CreateLessonCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EducationWriteSupport.Context<LessonWriteDto>(tenantContext) is { } unresolved) return unresolved;
        var parent = await parents.ReadAsync(request.CourseId, cancellationToken);
        if (parent is null) return EducationWriteSupport.Code<LessonWriteDto>("lockey_not_found");
        if (EducationWriteSupport.Scope<LessonWriteDto>(parent, tenantContext) is { } scopeFailure) return scopeFailure;
        var definition = await definitions.ReadContentTypeAsync(request.ContentTypeKey, request.ContentTypeSchemaVersion,
            DefinitionReadPurpose.NewBinding, cancellationToken);
        if (definition.IsFailure) return Result<LessonWriteDto>.Fail(definition.Error);
        var root = Lesson.Create(LessonId.From(request.LessonId), parent, request.Sort, request.ContentTypeKey,
            request.ContentTypeSchemaVersion, clock, tenantContext.UserId ?? UserId.SystemActor);
        auditSubject.Designate(root);
        return await EducationWriteSupport.SaveAsync(() => lessons.AddAsync(root, cancellationToken),
            () => EducationWriteSupport.Dto(root), unitOfWork);
    }
}

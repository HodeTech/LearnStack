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
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.SharedKernel.Validation;

using LearnStack.Modules.Education.Application.Contracts.Lessons;

namespace LearnStack.Modules.Education.Application.Lessons;

internal sealed class AddLessonTranslationCommandHandler(ILessonWriteStore roots, ITenantLocaleEligibilityReader locales, ITranslationCollisionReader collisions, IExactCustomizationDefinitionReader definitions, IJsonSchemaValidator schemas, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock)
    : IRequestHandler<AddLessonTranslationCommand, Result<LessonWriteDto>>
{
    public async Task<Result<LessonWriteDto>> Handle(AddLessonTranslationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EducationWriteSupport.Context<LessonWriteDto>(tenantContext) is { } unresolved) return unresolved;
        var root = await roots.FindAsync(LessonId.From(request.LessonId), cancellationToken);
        if (root is null) return EducationWriteSupport.Code<LessonWriteDto>("lockey_not_found");
        if (EducationWriteSupport.Scope<LessonWriteDto>(root, tenantContext) is { } scopeFailure) return scopeFailure;
        auditSubject.Designate(root);
        if (EducationWriteSupport.Version<LessonWriteDto>(root.Version, request.ExpectedVersion) is { } stale) return stale;
        if (EducationWriteSupport.Draft<LessonWriteDto>(root.Status) is { } lifecycle) return lifecycle;
        var locale = await locales.ReadEligibleAsync(request.Locale, cancellationToken);
        if (locale.IsFailure) return Result<LessonWriteDto>.Fail(locale.Error);

        var definition = await definitions.ReadContentTypeAsync(root.ContentTypeKey, root.ContentTypeSchemaVersion,
            DefinitionReadPurpose.ExistingPin, cancellationToken);
        if (definition.IsFailure) return Result<LessonWriteDto>.Fail(definition.Error);
        var body = schemas.ValidateInstance(definition.Value.JsonSchema, request.Body);
        if (body.IsFailure) return Result<LessonWriteDto>.Fail(body.Error);

        var added = root.AddTranslation(locale.Value, request.Title, request.Slug, request.Body, clock, tenantContext.UserId ?? UserId.SystemActor);
        if (added.IsFailure) return Result<LessonWriteDto>.Fail(added.Error);
        return await EducationWriteSupport.SaveAsync(() => roots.UpdateAsync(root, cancellationToken),
            () => EducationWriteSupport.Dto(root), unitOfWork, async constraint =>
            {
                if (constraint != "ux_lesson_translations_tenant_id_locale_slug") return null;
                var visible = await collisions.ReadLessonAsync(locale.Value, request.Slug, cancellationToken);
                return EducationWriteSupport.SlugConflict(locale.Value, request.Slug, visible?.Value);
            });
    }
}

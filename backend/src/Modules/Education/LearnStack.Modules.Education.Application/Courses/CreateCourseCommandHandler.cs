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
using LearnStack.Modules.Education.Application.Contracts.Courses;

namespace LearnStack.Modules.Education.Application.Courses;

internal sealed class CreateCourseCommandHandler(ICourseWriteStore courses,
    IExactCustomizationDefinitionReader definitions, ITenantContext tenantContext,
    IUnitOfWork unitOfWork, IAuditSubject auditSubject, IClock clock)
    : IRequestHandler<CreateCourseCommand, Result<CourseWriteDto>>
{
    public async Task<Result<CourseWriteDto>> Handle(CreateCourseCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EducationWriteSupport.Context<CourseWriteDto>(tenantContext) is { } unresolved) return unresolved;
        if (request.LevelTaxonomyKey is { } key)
        {
            var taxonomy = await definitions.ReadTaxonomyAsync(key, request.LevelTaxonomySchemaVersion!.Value,
                DefinitionReadPurpose.NewBinding, cancellationToken);
            if (taxonomy.IsFailure) return Result<CourseWriteDto>.Fail(taxonomy.Error);
            if (!taxonomy.Value.Bands.Any(band => band.Key == request.LevelBandKey))
                return EducationWriteSupport.Field<CourseWriteDto>("LevelBandKey", "lockey_education_band_invalid");
        }
        var access = request.ContentAccess == "public" ? CourseContentAccess.Public : CourseContentAccess.EnrollmentRequired;
        var root = Course.Create(CourseId.From(request.CourseId), tenantContext.TenantId, tenantContext.OrganizationId,
            request.SlugKey, access, clock, tenantContext.UserId ?? UserId.SystemActor,
            request.LevelTaxonomyKey, request.LevelTaxonomySchemaVersion, request.LevelBandKey);
        auditSubject.Designate(root);
        return await EducationWriteSupport.SaveAsync(() => courses.AddAsync(root, cancellationToken),
            () => EducationWriteSupport.Dto(root), unitOfWork);
    }
}

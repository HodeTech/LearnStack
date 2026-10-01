using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Education.Application.Seeding;

internal sealed class GetCourseSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context)
    : IRequestHandler<GetCourseSeedStateQuery, Result<SeedLookup<CourseSeedDto>>>
{
    public async Task<Result<SeedLookup<CourseSeedDto>>> Handle(
        GetCourseSeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved)
        {
            return Result<SeedLookup<CourseSeedDto>>.Fail(new Error(new LocalizedMessage("lockey_tenant_mismatch")));
        }

        return Result.Ok(new SeedLookup<CourseSeedDto>(
            await reader.ReadCourseAsync(request.CourseId, cancellationToken)));
    }
}

internal sealed class GetLessonSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context)
    : IRequestHandler<GetLessonSeedStateQuery, Result<SeedLookup<LessonSeedDto>>>
{
    public async Task<Result<SeedLookup<LessonSeedDto>>> Handle(
        GetLessonSeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved)
        {
            return Result<SeedLookup<LessonSeedDto>>.Fail(new Error(new LocalizedMessage("lockey_tenant_mismatch")));
        }

        return Result.Ok(new SeedLookup<LessonSeedDto>(
            await reader.ReadLessonAsync(request.LessonId, cancellationToken)));
    }
}

using FluentValidation;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Education.Application.Seeding;

internal sealed class GetCourseSeedStateQueryValidator : AbstractValidator<GetCourseSeedStateQuery>
{
    public GetCourseSeedStateQueryValidator()
    {
        RuleFor(request => request.CourseId).NotEmpty().WithErrorCode("lockey_identifier_required");
    }
}

internal sealed class GetLessonSeedStateQueryValidator : AbstractValidator<GetLessonSeedStateQuery>
{
    public GetLessonSeedStateQueryValidator()
    {
        RuleFor(request => request.LessonId).NotEmpty().WithErrorCode("lockey_identifier_required");
    }
}

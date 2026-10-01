using FluentValidation;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Customization.Application.Seeding;

internal sealed class GetContentTypeSeedStateQueryValidator : AbstractValidator<GetContentTypeSeedStateQuery>
{
    public GetContentTypeSeedStateQueryValidator()
    {
        RuleFor(request => request.ContentTypeId).NotEmpty().WithErrorCode("lockey_identifier_required");
    }
}

internal sealed class GetTaxonomySeedStateQueryValidator : AbstractValidator<GetTaxonomySeedStateQuery>
{
    public GetTaxonomySeedStateQueryValidator()
    {
        RuleFor(request => request.TaxonomyId).NotEmpty().WithErrorCode("lockey_identifier_required");
    }
}

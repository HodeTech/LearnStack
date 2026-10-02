using LearnStack.Modules.Customization.Application.Customization;
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

internal sealed class GetActiveContentTypeSeedRevisionQueryValidator : AbstractValidator<GetActiveContentTypeSeedRevisionQuery>
{
    public GetActiveContentTypeSeedRevisionQueryValidator()
    {
        RuleFor(request => request.Key).MustBeACustomizationKey();
    }
}

internal sealed class GetActiveTaxonomySeedRevisionQueryValidator : AbstractValidator<GetActiveTaxonomySeedRevisionQuery>
{
    public GetActiveTaxonomySeedRevisionQueryValidator()
    {
        RuleFor(request => request.Key).MustBeACustomizationKey();
    }
}

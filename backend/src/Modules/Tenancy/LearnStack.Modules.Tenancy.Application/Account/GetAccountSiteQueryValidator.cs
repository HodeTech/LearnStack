using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;

namespace LearnStack.Modules.Tenancy.Application.Account;

public sealed class GetAccountSiteQueryValidator : AbstractValidator<GetAccountSiteQuery>
{
    public GetAccountSiteQueryValidator() =>
        RuleFor(request => request.HasUnexpectedQuery).Equal(false)
            .OverridePropertyName("Query").WithErrorCode("lockey_invalid_value");
}

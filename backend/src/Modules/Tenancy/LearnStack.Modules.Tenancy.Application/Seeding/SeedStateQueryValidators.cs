using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.Seeding;

internal sealed class GetOrganizationSeedStateQueryValidator : AbstractValidator<GetOrganizationSeedStateQuery>
{
    public GetOrganizationSeedStateQueryValidator()
    {
        RuleFor(request => request.OrganizationId).Must(id => id.IsInitialized() && id.Value != Guid.Empty).WithErrorCode("lockey_identifier_required");
    }
}

internal sealed class GetHostMappingSeedStateQueryValidator : AbstractValidator<GetHostMappingSeedStateQuery>
{
    public GetHostMappingSeedStateQueryValidator()
    {
        RuleFor(request => request.Host).Must(host => EffectiveHost.Normalize(host) is not null).WithErrorCode("lockey_host_not_resolvable");
    }
}

internal sealed class GetSettingSeedStateQueryValidator : AbstractValidator<GetSettingSeedStateQuery>
{
    public GetSettingSeedStateQueryValidator()
    {
        RuleFor(request => request.SettingId).NotEmpty().WithErrorCode("lockey_identifier_required");
    }
}

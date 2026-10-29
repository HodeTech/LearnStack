using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Locales;

/// <summary>No original v1 seed act promotes a locale; human promotion has a separate protected request.</summary>
internal sealed class SetDefaultTenantLocaleCommandHandler(ITenantContext context)
    : IRequestHandler<SetDefaultTenantLocaleCommand, Result<TenantLocalesDto>>
{
    public Task<Result<TenantLocalesDto>> Handle(SetDefaultTenantLocaleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (TenantWriteFailures.Scope<TenantLocalesDto>(context) is { } refusal)
            return Task.FromResult(refusal);
        return Task.FromResult(TenantWriteFailures.Code<TenantLocalesDto>("lockey_business_rule_violation"));
    }
}

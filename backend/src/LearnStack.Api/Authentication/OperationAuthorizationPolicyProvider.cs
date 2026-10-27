using LearnStack.Application.Authorization;
using LearnStack.SharedKernel.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace LearnStack.Api.Authentication;

internal sealed class DeclaredOperationRequirement : IAuthorizationRequirement;
internal sealed record OperationHttpRefusal(LearnStack.SharedKernel.Results.Error Error);

/// <summary>Named requirements refer to the same frozen module-owned policies as application dispatch.</summary>
internal sealed class OperationAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options,
    IOperationPolicyCatalog catalog) : DefaultAuthorizationPolicyProvider(options)
{
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        if (!policyName.StartsWith(OperationPolicyRequirement.Prefix, StringComparison.Ordinal))
            return base.GetPolicyAsync(policyName);
        var policy = catalog.All.SingleOrDefault(policy => OperationPolicyRequirement.Prefix + policy.Id == policyName);
        return Task.FromResult(policy is { Kind: OperationPolicyKind.Protected or OperationPolicyKind.AccountRead }
            ? new AuthorizationPolicy([new OperationPolicyRequirement(policy)], [TenantAuthenticationRegistration.Scheme]) : null);
    }
}

internal sealed class OperationAuthorizationHandler(OperationHttpClassifier classifier,
    Func<OperationAuthorizationCoordinator> operations)
    : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext http) return;
        var classification = classifier.Classify(http.GetEndpoint(), out var policy);
        foreach (var requirement in context.PendingRequirements.ToArray())
        {
            if (requirement is DeclaredOperationRequirement)
            {
                if (classification is HttpOperationClassification.Unmatched or HttpOperationClassification.Infrastructure)
                    context.Succeed(requirement);
                else context.Fail();
            }
            else if (requirement is OperationPolicyRequirement requested)
            {
                if (classification != HttpOperationClassification.Application || !ReferenceEquals(policy, requested.Policy))
                {
                    context.Fail();
                    continue;
                }
                if (http.Features.Get<TenantCredentialFeature>()?.Identity is null)
                {
                    http.Features.Set(new OperationHttpRefusal(new(new LearnStack.SharedKernel.Localization.LocalizedMessage("lockey_not_found"))));
                    context.Fail();
                    continue;
                }
                var response = await operations().CheckHttpAsync(policy.RequestType, http.GetEndpoint()!, http.RequestAborted);
                if (response.IsSuccess) context.Succeed(requirement);
                else
                {
                    http.Features.Set(new OperationHttpRefusal(response.Error!));
                    context.Fail();
                }
            }
        }
    }
}

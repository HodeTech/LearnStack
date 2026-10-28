using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace LearnStack.Api.Authentication;

/// <summary>Keep the closed account read's no-authority result indistinguishable from an unknown host.</summary>
internal sealed class TenantAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (!authorizeResult.Succeeded && context.Features.Get<OperationHttpRefusal>() is { } refusal)
            return OperationClassificationMiddleware.RefuseAsync(context, refusal.Error);
        if (authorizeResult.Challenged
            && context.Features.Get<TenantCredentialFeature>()?.Identity is null
            && context.Features.Get<HostClassification>() is not null
            && TenantSurfaceAdmissionMiddleware.IsAccountSiteEndpoint(context))
        {
            // Invalid present credentials have already returned 401 at the
            // credential gate. Do not invoke the stock missing-token challenge.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return _fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}

using LearnStack.Api.Common;
using LearnStack.Api.PublicReads;
using LearnStack.Application.Authorization;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Authentication;

/// <summary>A matched handler requires a closed declaration even when it opts out of ASP.NET authorization.</summary>
internal sealed class OperationClassificationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, OperationHttpClassifier classifier,
        Func<OperationAuthorizationCoordinator> operations, ITenantContext tenant)
    {
        var classification = classifier.Classify(context.GetEndpoint(), out var policy);
        if (classification == HttpOperationClassification.Refused)
        {
            await RefuseAsync(context, new Error(new LocalizedMessage(
                tenant.IsResolved && context.Features.Get<TenantCredentialFeature>()?.Identity is not null
                    ? "lockey_forbidden" : "lockey_not_found")));
            return;
        }
        if (policy is { Kind: OperationPolicyKind.PublicRead })
        {
            if (!tenant.IsResolved || tenant.HostScope is null)
            {
                // Preserve the generic unresolved MediatR refusal; a resolved
                // claim-only context still cannot replace public host authority.
                await RefuseAsync(context, new Error(new LocalizedMessage(
                    tenant.IsResolved ? "lockey_not_found" : "lockey_tenant_mismatch")));
                return;
            }
            var response = await operations().CheckHttpAsync(policy.RequestType, context.GetEndpoint()!, context.RequestAborted);
            if (response.IsFailure)
            {
                await RefuseAsync(context, response.Error!);
                return;
            }
        }
        await next(context);
    }

    internal static async Task RefuseAsync(HttpContext context, Error error)
    {
        PublicResponsePolicy.Apply(context.Response);
        context.Response.StatusCode = HttpStatusMap.For(error);
        await context.Response.WriteAsJsonAsync(ProblemDetailsFactory.For(error, context), options: null,
            contentType: ProblemDetailsMediaType.Value, cancellationToken: context.RequestAborted);
    }
}

internal static class OperationClassificationExtensions
{
    internal static IApplicationBuilder UseLearnStackOperationClassification(this IApplicationBuilder app) =>
        app.UseMiddleware<OperationClassificationMiddleware>();
}

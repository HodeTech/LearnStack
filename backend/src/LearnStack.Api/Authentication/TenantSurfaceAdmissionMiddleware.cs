using LearnStack.Api.Account;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LearnStack.Api.Authentication;

/// <summary>HTTP admission for the one host-backed authenticated read.</summary>
internal sealed class TenantSurfaceAdmissionMiddleware(RequestDelegate next)
{
    internal const string AccountSitePath = "/api/v1/account/site";

    internal static bool IsAccountSitePath(PathString path) =>
        path.Equals(AccountSitePath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(AccountSitePath + "/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAccountSiteEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
        && action.ControllerTypeInfo.AsType() == typeof(AccountController)
        && action.MethodInfo.Name == nameof(AccountController.GetSite);

    public Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);
        if (context.Features.Get<HostClassification>() is not null
            && (IsAccountSiteEndpoint(context)
                || IsAccountSitePath(context.Request.Path))
            && (context.Features.Get<TenantCredentialFeature>()?.Identity is not { } identity
                || !HasHostAuthority(tenantContext, identity)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool HasHostAuthority(ITenantContext context, ValidatedTenantIdentity identity)
    {
        // The application query repeats this independent ceiling before any reader.
        // Neither authentication nor future platform-host membership supplies a host.
        if (!context.IsResolved || context.Origin != TenantContextOrigin.HostAndClaim
            || context.UserId is not { } actor || !StronglyTypedId.IsAssigned(actor)
            || actor == UserId.SystemActor || actor != identity.ActorUserId
            || context.HostScope is not { } host)
            return false;

        return StronglyTypedId.IsAssigned(context.TenantId)
            && context.TenantId != TenantId.PlatformSentinel
            && StronglyTypedId.IsAssigned(host.TenantId)
            && host.TenantId == context.TenantId
            && identity.TenantId == context.TenantId
            && StronglyTypedId.IsAssignedWhenPresent(identity.OrganizationId)
            && context.OrganizationId == (identity.OrganizationId ?? host.OrganizationId)
            && StronglyTypedId.IsAssignedWhenPresent(context.OrganizationId)
            && StronglyTypedId.IsAssignedWhenPresent(host.OrganizationId)
            && (host.OrganizationId is null || host.OrganizationId == context.OrganizationId);
    }
}

internal static class TenantSurfaceAdmissionExtensions
{
    internal static IApplicationBuilder UseLearnStackTenantSurfaceAdmission(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantSurfaceAdmissionMiddleware>();
    }
}

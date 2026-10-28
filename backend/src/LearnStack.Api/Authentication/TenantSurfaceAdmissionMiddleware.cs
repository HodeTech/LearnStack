using LearnStack.Api.Account;
using LearnStack.Api.Identity;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LearnStack.Api.Authentication;

/// <summary>Preserves the distinct authority ceilings of the two authenticated reads.</summary>
internal sealed class TenantSurfaceAdmissionMiddleware(RequestDelegate next)
{
    internal const string AccountSitePath = "/api/v1/account/site";
    internal const string MembershipsPath = "/api/v1/identity/memberships";
    internal const string NativeSessionAdmissionPath = "/api/v1/identity/session-scope/admissions";

    internal static bool IsNativeSessionAdmissionPath(PathString path) =>
        path.Equals(NativeSessionAdmissionPath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(NativeSessionAdmissionPath + "/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsNativeSessionAdmissionEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
        && action.ControllerTypeInfo.AsType() == typeof(NativeSessionAdmissionController)
        && action.MethodInfo.Name == nameof(NativeSessionAdmissionController.Admit);

    internal static bool IsMembershipsPath(PathString path) =>
        path.Equals(MembershipsPath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(MembershipsPath + "/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMembershipsEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
        && action.ControllerTypeInfo.AsType() == typeof(MembershipsController)
        && action.MethodInfo.Name == nameof(MembershipsController.List);

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
        if (IsNativeSessionAdmissionEndpoint(context) || IsNativeSessionAdmissionPath(context.Request.Path))
        {
            if (tenantContext.Origin != TenantContextOrigin.HostAndNativeSessionScope
                || context.Features.Get<NativeScopeEvidence>()?.IsCurrent(context) is not true)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
        }
        if (context.Features.Get<HostClassification>() is not null
            && (IsMembershipsEndpoint(context) || IsMembershipsPath(context.Request.Path))
            && (context.Features.Get<TenantCredentialFeature>()?.Identity is null
                || !tenantContext.IsResolved
                || tenantContext.Origin is not (TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership
                    or TenantContextOrigin.HostAndNativeSessionScope)
                || (tenantContext.Origin == TenantContextOrigin.HostAndNativeSessionScope
                    && context.Features.Get<NativeScopeEvidence>()?.IsCurrent(context) is not true)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        if (context.Features.Get<HostClassification>() is not null
            && (IsAccountSiteEndpoint(context)
                || IsAccountSitePath(context.Request.Path))
            && (context.Features.Get<TenantCredentialFeature>()?.Identity is not { } identity
                || !HasHostAuthority(context, tenantContext, identity)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool HasHostAuthority(HttpContext request, ITenantContext context, ValidatedTenantIdentity identity)
    {
        // The application query repeats this independent ceiling before any reader.
        // Neither authentication nor future platform-host membership supplies a host.
        if (!context.IsResolved || context.Origin is not (TenantContextOrigin.HostAndClaim or TenantContextOrigin.HostAndNativeSessionScope)
            || context.UserId is not { } actor || !StronglyTypedId.IsAssigned(actor)
            || actor == UserId.SystemActor || actor != identity.ActorUserId
            || context.HostScope is not { } host)
            return false;

        if (!StronglyTypedId.IsAssigned(context.TenantId)
            || context.TenantId == TenantId.PlatformSentinel
            || !StronglyTypedId.IsAssigned(host.TenantId) || host.TenantId != context.TenantId
            || !StronglyTypedId.IsAssignedWhenPresent(context.OrganizationId)
            || !StronglyTypedId.IsAssignedWhenPresent(host.OrganizationId)
            || (host.OrganizationId is not null && host.OrganizationId != context.OrganizationId))
            return false;

        if (context.Origin == TenantContextOrigin.HostAndNativeSessionScope)
            return request.Features.Get<NativeScopeEvidence>() is { } native
                && native.IsCurrent(request) && native.Actor == actor
                && native.Tenant == context.TenantId && native.Organization == context.OrganizationId;

        return identity.TenantId == context.TenantId
            && StronglyTypedId.IsAssignedWhenPresent(identity.OrganizationId)
            && context.OrganizationId == (identity.OrganizationId ?? host.OrganizationId);
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

using LearnStack.Api.Authentication;
using LearnStack.Api.Versioning;

namespace LearnStack.Api.PublicReads;

/// <summary>Owns no-store headers for public/account responses and authentication/admission refusals.</summary>
public static class PublicResponsePolicy
{
    public static IApplicationBuilder UseLearnStackPublicResponsePolicy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (!TenantSurfaceAdmissionMiddleware.IsAccountSitePath(context.Request.Path)
                && GlobalSelfOperation.ForPath(context.Request.Path) is null
                && !TenantSurfaceAdmissionMiddleware.IsNativeSessionAdmissionPath(context.Request.Path)
                && !TenantSurfaceAdmissionMiddleware.IsNativeSessionAdmissionEndpoint(context)
                && !TenantSurfaceAdmissionMiddleware.IsAccountSiteEndpoint(context)
                && !TenantSurfaceAdmissionMiddleware.IsMembershipsPath(context.Request.Path)
                && !TenantSurfaceAdmissionMiddleware.IsMembershipsEndpoint(context)
                && !ApiVersioningExtensions.LiveMajors.Any(major =>
                    context.Request.Path.StartsWithSegments($"/api/v{major}/public", StringComparison.OrdinalIgnoreCase)))
            {
                await next(context);
                return;
            }

            Apply(context.Response);
            context.Response.OnStarting(() => { Apply(context.Response); return Task.CompletedTask; });
            // TestServer does not implement Kestrel's HEAD body suppression. Keep the
            // shared error writers and success mapping identical, but discard all bytes.
            var body = context.Response.Body;
            if (HttpMethods.IsHead(context.Request.Method)) context.Response.Body = Stream.Null;
            try { await next(context); }
            finally { context.Response.Body = body; }
        });
    }

    internal static void Apply(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Remove("ETag");
        response.Headers.Remove("Last-Modified");
    }
}

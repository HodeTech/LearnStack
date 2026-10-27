using LearnStack.Api.Tenancy;
using LearnStack.Api.PublicReads;

namespace LearnStack.Api.Authentication;

/// <summary>A failed present credential cannot fall through to an anonymous endpoint.</summary>
internal sealed class TenantCredentialAdmissionMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<TenantCredentialFeature>() is { } feature) feature.Identity = null;
        if (context.Features.Get<HostClassification>() is not null
            && context.Request.Headers.ContainsKey("Authorization")
            && context.Features.Get<TenantCredentialFeature>()?.VerifiedSubject is null)
        {
            PublicResponsePolicy.Apply(context.Response);
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            // The outer status-code owner supplies the shared Problem Details body.
            return Task.CompletedTask;
        }

        return next(context);
    }
}

internal static class TenantCredentialAdmissionExtensions
{
    internal static IApplicationBuilder UseLearnStackTenantCredentialAdmission(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantCredentialAdmissionMiddleware>();
    }
}

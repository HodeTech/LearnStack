using System.Globalization;
using System.Threading.RateLimiting;
using LearnStack.Api.Tenancy;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Authentication;

/// <summary>Runs after the credential gate and before any tenant reconciliation/audit work.</summary>
internal sealed class TenantSubjectAdmissionMiddleware(RequestDelegate next, TenantSubjectRateLimiter limiter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<HostClassification>() is null
            || context.Features.Get<TenantCredentialFeature>()?.Identity is not { } identity)
        {
            await next(context);
            return;
        }

        var outcome = context.Features.Get<TenantSubjectAdmissionOutcome>();
        if (outcome is null)
        {
            using var lease = limiter.AttemptAcquire(identity);
            outcome = new TenantSubjectAdmissionOutcome(lease.IsAcquired,
                lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? retryAfter : TenantSubjectRateLimiter.Window);
            context.Features.Set(outcome);
        }

        if (outcome.Acquired)
        {
            await next(context);
            return;
        }

        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(outcome.RetryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);
        // The existing outer status-code pages produce the one standard body.
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    }

    private sealed record TenantSubjectAdmissionOutcome(bool Acquired, TimeSpan RetryAfter);
}

/// <summary>Prepared component registration; production activation belongs to P02b-5 Step 4.</summary>
internal static class TenantSubjectAdmissionExtensions
{
    internal static IServiceCollection AddLearnStackTenantSubjectAdmission(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TenantSubjectRateLimiter>();
        return services;
    }

    internal static IApplicationBuilder UseLearnStackTenantSubjectAdmission(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantSubjectAdmissionMiddleware>();
    }
}

using System.Globalization;
using LearnStack.Api.Common;
using LearnStack.Api.PublicReads;
using LearnStack.Api.Tenancy;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Authentication;

/// <summary>Runs after the credential gate and before any tenant reconciliation/audit work.</summary>
internal sealed class TenantSubjectAdmissionMiddleware(RequestDelegate next, TenantSubjectRateLimiter limiter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var feature = context.Features.Get<TenantCredentialFeature>();
        if (feature is not null) feature.Identity = null;
        if (context.Features.Get<HostClassification>() is null)
        {
            await next(context);
            return;
        }

        var provenance = context.Features.Get<TenantSubjectAdmissionProvenance>();
        var outcome = context.Features.Get<SubjectAdmissionDecision>();
        if (feature?.VerifiedSubject is not { } identity)
        {
            if (provenance is not null || outcome is not null) TenantIdentityBindingAdmissionMiddleware.Refuse(context);
            else await next(context);
            return;
        }
        if ((provenance is null) != (outcome is null)
            || provenance is not null && !provenance.Matches(identity))
        {
            TenantIdentityBindingAdmissionMiddleware.Refuse(context);
            return;
        }

        if (outcome is null)
        {
            outcome = limiter.AttemptAcquire(identity);
            context.Features.Set(new TenantSubjectAdmissionProvenance(identity));
            context.Features.Set(outcome);
            if (!outcome.IsAcquired)
                TenantSubjectAdmissionMetrics.RecordRejected(outcome.Outcome);
        }

        if (outcome.IsAcquired)
        {
            await next(context);
            return;
        }

        // Subject admission also protects classified, unmatched API paths.
        // Their refusals need the same policy as known public/account routes.
        PublicResponsePolicy.Apply(context.Response);
        if (outcome.Outcome == SubjectAdmissionOutcome.RegistryFault)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            RetryAfter.Apply(context.Response, context.Response.StatusCode);
            return;
        }

        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(
                (outcome.RetryAfter ?? TenantSubjectRateLimiter.Window).TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);
        // The existing outer status-code pages produce the one standard body.
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    }

}

/// <summary>A cached subject decision can be reused only with its original immutable verified evidence.</summary>
internal sealed class TenantSubjectAdmissionProvenance
{
    private readonly VerifiedProviderSubject _subject;

    internal TenantSubjectAdmissionProvenance(VerifiedProviderSubject subject) => _subject = subject;

    internal bool Matches(VerifiedProviderSubject candidate) => ReferenceEquals(_subject, candidate);
}

/// <summary>Registers the owned additional subject budget used by the closed tenant HTTP boundary.</summary>
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

using LearnStack.SharedKernel.Observability;
using Sentry;

namespace LearnStack.Infrastructure.ErrorTracking;

/// <summary>
/// <see cref="IErrorTrackingProvider"/> implementation that dispatches to
/// Sentry. The Sentry hub is supplied by the SDK once
/// <c>SentrySdk.Init</c> has been called by the composition root.
/// </summary>
/// <remarks>
/// Per ADR-0032 § Sub-decision 9 this is the only sanctioned site that
/// references the Sentry SDK; modules import <see cref="IErrorTrackingProvider"/>
/// instead. The architecture test
/// <c>Modules_Do_Not_Reference_Sentry_SDK_Directly</c> enforces the scope.
/// </remarks>
internal sealed class SentryErrorTracker : IErrorTrackingProvider
{
    public ValueTask CaptureAsync(
        Exception exception,
        CapturedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        SentrySdk.CaptureException(exception, scope =>
        {
            if (context.TenantId is { } tenantId)
            {
                scope.SetTag("tenant.id", tenantId.ToString());
            }

            if (context.OrganizationId is { } orgId)
            {
                scope.SetTag("organization.id", orgId.ToString());
            }

            if (context.UserId is { } userId)
            {
                scope.User = new SentryUser { Id = userId.ToString() };
            }

            if (!string.IsNullOrWhiteSpace(context.CorrelationId))
            {
                scope.SetTag("correlation.id", context.CorrelationId);
            }

            if (!string.IsNullOrWhiteSpace(context.ModuleName))
            {
                scope.SetTag("module", context.ModuleName);
            }

            if (!string.IsNullOrWhiteSpace(context.RequestPath))
            {
                scope.SetTag("http.route", context.RequestPath);
            }

            if (!string.IsNullOrWhiteSpace(context.RequestMethod))
            {
                scope.SetTag("http.method", context.RequestMethod);
            }

            if (context.AdditionalTags is not null)
            {
                foreach (var (key, value) in context.AdditionalTags)
                {
                    scope.SetTag(key, value);
                }
            }
        });

        return ValueTask.CompletedTask;
    }
}

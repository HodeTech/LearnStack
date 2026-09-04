using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Secrets;
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
            // Value.ToString() under an IsInitialized() gate on every one of the
            // three. A Vogen id's own ToString() renders "[UNINITIALIZED]" for an
            // unassigned value, and these tags are what a dashboard groups by;
            // reading Value without the gate throws, inside the handler that is
            // already reporting someone else's exception.
            if (context.TenantId is { } tenantId && tenantId.IsInitialized())
            {
                scope.SetTag("tenant.id", tenantId.Value.ToString());
            }

            if (context.OrganizationId is { } orgId && orgId.IsInitialized())
            {
                scope.SetTag("organization.id", orgId.Value.ToString());
            }

            if (context.UserId is { } userId && userId.IsInitialized())
            {
                scope.User = new SentryUser { Id = userId.Value.ToString() };
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
                    // Redact sensitive tag values before they leave the
                    // process — Sentry is external egress. Uses the same
                    // SensitiveTokenCatalog the Serilog enricher + the
                    // air-gapped LocalFileErrorTracker share so the three
                    // surfaces cannot drift (Standards 11 § Sensitive Data
                    // Exposure).
                    scope.SetTag(
                        key,
                        SensitiveTokenCatalog.IsSensitive(key)
                            ? SensitiveTokenCatalog.RedactedValue
                            : value);
                }
            }
        });

        return ValueTask.CompletedTask;
    }
}

namespace LearnStack.SharedKernel.Observability;

/// <summary>
/// Sanctioned entry point for error capture. Per
/// <see href="../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
/// § Sub-decision 9</see> the L1 <c>IExceptionHandler</c> is the only
/// production caller; modules never import <c>Sentry.SentrySdk</c> directly.
/// The composition root selects the implementation by <c>DeploymentMode</c>
/// (<see cref="LearnStack.SharedKernel.Hosting.DeploymentMode"/>):
/// <c>NoOpErrorTracker</c> for Development, <c>SentryErrorTracker</c> for
/// SaaS / Dedicated / SelfHostedOnline (DSN via <c>ISecretProvider</c>),
/// <c>LocalFileErrorTracker</c> for SelfHostedAirGapped.
/// </summary>
/// <remarks>
/// The capture boundary itself (which exceptions go here and which only tag
/// the OTel span) lives in
/// <c>LearnStack.Api.Common.LearnStackExceptionHandler.ShouldCapture</c> per
/// Standards 09 § Sentry vs OpenTelemetry — Error Capture Boundary.
/// </remarks>
public interface IErrorTrackingProvider
{
    ValueTask CaptureAsync(
        Exception exception,
        CapturedContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Snapshot of cross-cutting tags every error capture flows with. The L1
/// handler builds it from the current <c>HttpContext</c> + the singleton
/// <c>ITenantContextAccessor</c>; offline capture sites (the local-file
/// tracker, future worker hosts) build it themselves.
/// </summary>
public sealed record CapturedContext(
    string? CorrelationId,
    string? RequestPath,
    string? RequestMethod,
    Guid? TenantId,
    Guid? OrganizationId,
    Guid? UserId,
    string? ModuleName,
    IReadOnlyDictionary<string, string>? AdditionalTags = null);

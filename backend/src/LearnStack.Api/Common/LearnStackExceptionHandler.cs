using System.Diagnostics;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LearnStack.Api.Common;

/// <summary>
/// L1 exception handler — the single catch site at the HTTP boundary per
/// ADR-0032 § Sub-decision 1. Builds the Problem Details body, records the
/// span error, and dispatches to <see cref="IErrorTrackingProvider"/> only
/// when <see cref="ShouldCapture(Exception)"/> returns <c>true</c>
/// (Standards 09 § Sentry vs OpenTelemetry — Error Capture Boundary).
/// </summary>
/// <remarks>
/// <c>internal sealed</c> — modules do not (and per ADR-0032 § Sub-decision 1
/// must not) instantiate it; the framework's
/// <c>services.AddExceptionHandler&lt;T&gt;()</c> is the only entry. Tests
/// reach the type through <c>InternalsVisibleTo</c>.
/// </remarks>
internal sealed class LearnStackExceptionHandler(
    IErrorTrackingProvider errorTracker,
    ITenantContextAccessor tenantContextAccessor,
    ILogger<LearnStackExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var problem = ProblemDetailsFactory.For(exception, httpContext);
        var capture = ShouldCapture(exception);
        var isProviderClientError = exception is ProviderException { IsClientError: true };
        var isCancellation = exception is OperationCanceledException;

        // Span semantics per Standards 09 § Sentry vs OpenTelemetry table:
        //   OperationCanceled   → leave span Unset, no RecordException
        //   Provider 4xx        → SetStatus(Error), no RecordException
        //   everything else     → RecordException + SetStatus(Error)
        // Activity.AddException is the .NET 9+ replacement for the legacy
        // Activity.RecordException — the ADR's Implementation Notes still
        // reference the older name; both add the same exception.* tags.
        if (!isCancellation)
        {
            if (!isProviderClientError)
            {
                Activity.Current?.AddException(exception);
            }

            Activity.Current?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        }

        if (capture)
        {
            var capturedContext = BuildCapturedContext(httpContext);
            await errorTracker.CaptureAsync(exception, capturedContext, cancellationToken)
                .ConfigureAwait(false);
            LogCaptured(logger, exception.GetType().FullName ?? "<unknown>", exception);
        }
        else
        {
            LogSkipped(logger, exception.GetType().FullName ?? "<unknown>", null);
        }

        // OperationCanceled means the client has already disconnected. The
        // outbound flush would throw on the closed socket anyway and the
        // body would not reach a reader. Set the status for completeness
        // and skip the body.
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        if (isCancellation || cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(
                problem,
                problem.GetType(),
                options: null,
                contentType: "application/problem+json",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// The Sentry / OTel boundary table (Standards 09 § Sentry vs
    /// OpenTelemetry — Error Capture Boundary) reduced to a switch. Internal
    /// for unit-test visibility via <c>InternalsVisibleTo</c>; the rule
    /// itself is binding from ADR-0032 § Sub-decision 7.
    /// </summary>
    internal static bool ShouldCapture(Exception exception) => exception switch
    {
        OperationCanceledException => false,
        ProviderException pex when pex.IsClientError => false,
        _ => true,
    };

    private CapturedContext BuildCapturedContext(HttpContext httpContext)
    {
        var context = tenantContextAccessor.Current;
        var traceId = Activity.Current?.TraceId.ToString();

        return new CapturedContext(
            CorrelationId: traceId ?? context?.CorrelationId,
            RequestPath: httpContext.Request.Path.Value,
            RequestMethod: httpContext.Request.Method,
            TenantId: context?.IsResolved == true ? context.TenantId : null,
            OrganizationId: context?.OrganizationId,
            UserId: context?.UserId?.Value,
            ModuleName: context?.ModuleName,
            AdditionalTags: null);
    }

    private static readonly Action<ILogger, string, Exception?> LogCaptured =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(LogCaptured)),
            "L1 exception handler captured {ExceptionType} to IErrorTrackingProvider.");

    private static readonly Action<ILogger, string, Exception?> LogSkipped =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(LogSkipped)),
            "L1 exception handler skipped Sentry capture for {ExceptionType} per Standards 09 boundary.");
}

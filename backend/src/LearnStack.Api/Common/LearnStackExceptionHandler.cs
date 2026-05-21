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
public sealed class LearnStackExceptionHandler(
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

        // OperationCanceled stays on Unset — RecordException would tag the
        // span as error and Tempo would render the client disconnect as a
        // failure. See ADR-0032 § Sub-decision 7 + Implementation Notes.
        if (exception is not OperationCanceledException)
        {
            Activity.Current?.AddException(exception);
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

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
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

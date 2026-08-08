using System.Diagnostics;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 2 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Opens an <see cref="ILogger.BeginScope"/>
/// carrying the eight correlation fields (Standards 10 § Correlation),
/// starts a manual <see cref="Activity"/> named <c>mediatr.&lt;RequestName&gt;</c>
/// on the <c>learnstack.mediatr</c> <see cref="ActivitySource"/>, and
/// measures the handler latency for downstream histogram reporting.
/// </summary>
/// <remarks>
/// <para>
/// The actual metric histogram is wired into the OpenTelemetry meter at the
/// composition root; this behavior simply records start / stop on a
/// per-invocation <see cref="Stopwatch"/> and attaches the elapsed
/// milliseconds to the log scope so the metric pipeline can pick it up.
/// </para>
/// <para>
/// The <c>ActivitySource</c> name is <c>"learnstack.mediatr"</c>; per-module
/// manual spans use their own source (e.g. <c>"learnstack.education"</c>) so
/// trace consumers can filter independently.
/// </para>
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger,
    ITenantContextAccessor tenantContextAccessor)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    private static readonly ActivitySource ActivitySource = new("learnstack.mediatr");

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var requestName = typeof(TRequest).Name;
        var context = tenantContextAccessor.Current;

        using var activity = ActivitySource.StartActivity(
            $"mediatr.{requestName}",
            ActivityKind.Internal);

        using var scope = logger.BeginScope(BuildScope(context, requestName));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next().ConfigureAwait(false);
            stopwatch.Stop();

            if (response.IsSuccess)
            {
                LogSuccess(logger, requestName, stopwatch.ElapsedMilliseconds, null);
            }
            else
            {
                LogFailure(logger, requestName, response.Error?.Code ?? "<unknown>", stopwatch.ElapsedMilliseconds, null);
            }

            return response;
        }
        catch
        {
            stopwatch.Stop();
            // Re-throw to preserve the catch / audit / rethrow contract carried
            // by AuditLogBehavior (step 3). LoggingBehavior is intentionally
            // silent on exception so AuditLogBehavior owns the failure-audit
            // path; the L1 handler logs the exception once at the boundary.
            throw;
        }
    }

    private static Dictionary<string, object?> BuildScope(ITenantContext? context, string requestName)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["RequestName"] = requestName,
            ["TenantId"] = context?.IsResolved == true ? context.TenantId : null,
            ["OrganizationId"] = context?.OrganizationId,
            ["UserId"] = context?.UserId?.Value,
            ["CorrelationId"] = context?.CorrelationId,
            ["Module"] = context?.ModuleName,
        };
    }

    // LoggerMessage source-generated delegates (CA1848) — keep the format
    // strings identical to the inlined-string version they replaced.
    private static readonly Action<ILogger, string, long, Exception?> LogSuccess =
        LoggerMessage.Define<string, long>(
            LogLevel.Information,
            new EventId(1, nameof(LogSuccess)),
            "MediatR request {RequestName} completed successfully in {ElapsedMilliseconds} ms");

    private static readonly Action<ILogger, string, string, long, Exception?> LogFailure =
        LoggerMessage.Define<string, string, long>(
            LogLevel.Information,
            new EventId(2, nameof(LogFailure)),
            "MediatR request {RequestName} returned Result.Fail({ErrorCode}) in {ElapsedMilliseconds} ms");
}

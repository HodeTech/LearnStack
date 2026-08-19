using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LearnStack.Api.Common;

/// <summary>
/// Puts the request's correlation id on the response, so a caller can quote it
/// without having triggered an error first.
/// </summary>
/// <remarks>
/// <para>
/// <see href="../../../../docs/standards/10-observability.md">Standards 10
/// § Correlation</see> makes <c>correlation_id</c> the full W3C traceparent
/// (<c>Activity.Current.Id</c>) and requires it on the Problem Details body and
/// on error-tracker captures. Both already carry it. What was missing is the
/// success path: a client reporting "this page rendered the wrong thing" had no
/// handle at all, because the only way to obtain one was to receive an error.
/// </para>
/// <para>
/// <b>The inbound header is ignored entirely.</b>
/// <see href="../../../../docs/architecture/30-api-gateway.md">architecture/30</see>
/// has APISIX inject <c>X-Correlation-Id</c>, but the identity is the trace
/// context — adopting a client's value would let two unrelated requests share a
/// correlation id, or let one request poison a log search. Cross-service
/// correlation is already handled properly by W3C <c>traceparent</c>
/// propagation.
/// </para>
/// <para>
/// A first version <i>echoed</i> the client's value back under a second header,
/// on the theory that a caller threading its own id should keep it. That was
/// wrong twice. The caller already knows what it sent, so the echo bought
/// nothing — and Kestrel accepts bytes in a <b>request</b> header that it
/// refuses to write into a <b>response</b> header, so <c>é</c>, a control
/// character or an emoji made the assignment throw. Measured: a 500 on every
/// route, pre-auth and pre-routing, each one captured by
/// <c>IErrorTrackingProvider</c> — an anonymous client owning the Sentry quota
/// with one header. Exactly the failure
/// <see cref="LearnStack.SharedKernel.Tenancy.EffectiveHost"/> was made total to
/// avoid, shipped one file away from it.
/// </para>
/// </remarks>
public sealed class CorrelationHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;

            // Activity.Current.Id, not TraceIdentifier: the same value
            // ProblemDetailsFactory puts on an error body and the error tracker
            // tags a capture with, so all three correlate. TraceIdentifier is a
            // per-connection Kestrel string that appears nowhere else.
            var correlationId = Activity.Current?.Id ?? http.TraceIdentifier;
            http.Response.Headers[HeaderName] = correlationId;

            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}

/// <summary>Registration for <see cref="CorrelationHeaderMiddleware"/>.</summary>
public static class CorrelationHeaderMiddlewareExtensions
{
    public static IApplicationBuilder UseLearnStackCorrelationHeader(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CorrelationHeaderMiddleware>();
    }
}

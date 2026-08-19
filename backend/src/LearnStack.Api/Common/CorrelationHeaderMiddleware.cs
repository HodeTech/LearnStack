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
/// <b>The inbound header is echoed, never trusted as the id.</b>
/// <see href="../../../../docs/architecture/30-api-gateway.md">architecture/30</see>
/// has APISIX inject <c>X-Correlation-Id</c>, and until the gateway lands the
/// value would otherwise be whatever a client typed — which would let two
/// unrelated requests share a correlation id, or one request poison a log
/// search. The trace context is the identity; the header is a copy of it.
/// A client-supplied value is preserved under a separate name so a caller that
/// threads its own id keeps it, without either value pretending to be the
/// other.
/// </para>
/// </remarks>
public sealed class CorrelationHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// Where a client-supplied correlation id is echoed back. Separate from
    /// <see cref="HeaderName"/> because they are different things: one is what
    /// this system will log the request under, the other is what the caller
    /// asked us to remember.
    /// </summary>
    public const string RequestHeaderName = "X-Request-Correlation-Id";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var inbound = context.Request.Headers[HeaderName];

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

        if (inbound.Count == 1 && !string.IsNullOrWhiteSpace(inbound[0]))
        {
            // Bounded and echoed verbatim under its own name. It is
            // attacker-controlled, so it is capped before it can be logged by
            // anything downstream.
            var supplied = inbound[0]!;
            context.Response.Headers[RequestHeaderName] =
                supplied.Length > 128 ? supplied[..128] : supplied;
        }

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

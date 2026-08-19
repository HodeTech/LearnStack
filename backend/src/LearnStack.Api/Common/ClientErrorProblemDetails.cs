using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace LearnStack.Api.Common;

/// <summary>
/// Gives a LearnStack Problem Details body to the client errors nothing else
/// covers — the ones the framework produces with no body at all.
/// </summary>
/// <remarks>
/// <para>
/// Standards 04 § Error Responses admits exactly one error shape and
/// Standards 09 § API Surface fixes its fields. Three statuses arrived outside
/// it, from two different places:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>404 and 405</b> come from <b>routing</b>, before MVC. No action runs, so
/// no MVC hook fires — <see cref="MapLearnStackClientErrors"/>, built on
/// <c>UseStatusCodePages</c>, is what sees them.
/// </item>
/// <item>
/// <b>415</b> and any bodyless <see cref="StatusCodeResult"/> come from
/// <b>MVC</b>. <see cref="ApiControllerAttribute"/> already converts those into
/// ASP.NET's own <see cref="ProblemDetails"/> — the right idea, the wrong
/// shape, with no <c>code</c>, no <c>messageKey</c> and no
/// <c>correlationId</c>. <see cref="LearnStackClientErrorFactory"/> replaces
/// that conversion rather than layering over it.
/// </item>
/// </list>
/// <para>
/// Both funnel through <see cref="ProblemDetailsFactory.ForStatus"/>, so a
/// 404 from routing and a 404 from a controller are indistinguishable on the
/// wire — which is the point of having one shape.
/// </para>
/// </remarks>
public static class ClientErrorProblemDetails
{
    /// <summary>
    /// Writes a Problem Details body for any 4xx/5xx response that reached the
    /// client with none.
    /// </summary>
    /// <remarks>
    /// Registered right after <c>UseExceptionHandler</c>. "Before routing" is
    /// not something a caller can arrange in minimal hosting — the implicit
    /// <c>UseRouting</c> is inserted ahead of user middleware — so what
    /// actually matters is the order relative to the exception handler, and
    /// that a routing 404 unwinds back through this middleware on the way out.
    /// </remarks>
    public static WebApplication MapLearnStackClientErrors(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;

            // UseStatusCodePages only invokes this for an empty body, but the
            // guard is cheap and the alternative — a second body appended to a
            // partial response — is a malformed payload rather than an error.
            if (http.Response.HasStarted)
            {
                return;
            }

            // An exception that reached the L1 handler has already produced a
            // body and a status; re-writing it here would replace a specific
            // error with a generic one derived from the status alone.
            if (http.Features.Get<IExceptionHandlerFeature>() is not null)
            {
                return;
            }

            var problem = ProblemDetailsFactory.ForStatus(http.Response.StatusCode, http);

            // The content type goes through WriteAsJsonAsync, not through a
            // prior assignment to Response.ContentType: the method sets its own
            // ("application/json") and silently overwrites anything already
            // there, which is how these two answered with the right body under
            // the wrong media type.
            await http.Response.WriteAsJsonAsync(
                problem,
                options: null,
                contentType: ProblemDetailsMediaType.Value,
                cancellationToken: http.RequestAborted);
        });

        return app;
    }
}

/// <summary>
/// Replaces MVC's client-error conversion so a bodyless
/// <see cref="StatusCodeResult"/> from an <see cref="ApiControllerAttribute"/>
/// action carries the LearnStack shape rather than ASP.NET's.
/// </summary>
internal sealed class LearnStackClientErrorFactory : IClientErrorFactory
{
    public IActionResult GetClientError(ActionContext actionContext, IClientErrorActionResult clientError)
    {
        ArgumentNullException.ThrowIfNull(actionContext);
        ArgumentNullException.ThrowIfNull(clientError);

        var status = clientError.StatusCode ?? StatusCodes.Status500InternalServerError;

        // actionContext.HttpContext, not an injected IHttpContextAccessor: MVC
        // hands the context in, and registering the accessor app-wide to reach
        // something already in the parameter list costs an AsyncLocal write on
        // every request for nothing.
        var problem = ProblemDetailsFactory.ForStatus(status, actionContext.HttpContext);

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { ProblemDetailsMediaType.Value },
        };
    }
}

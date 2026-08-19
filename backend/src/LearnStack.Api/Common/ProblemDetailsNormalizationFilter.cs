using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LearnStack.Api.Common;

/// <summary>
/// Rewrites any client- or server-error result that is not already a LearnStack
/// Problem Details into one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LearnStackClientErrorFactory"/> covers only
/// <see cref="Microsoft.AspNetCore.Mvc.Infrastructure.IClientErrorActionResult"/>,
/// and — measured — the <c>*ObjectResult</c> family does not implement it:
/// <c>NotFoundResult</c> does, <c>NotFoundObjectResult</c> does not. So
/// <c>NotFound()</c> produced the right shape while <c>NotFound(new {...})</c>,
/// <c>BadRequest(body)</c>, <c>Conflict(body)</c>, <c>ValidationProblem()</c>
/// and <c>Problem()</c> all shipped ASP.NET's shape or raw JSON. That is the
/// single most idiomatic line a controller author writes, and Standards 04
/// § Error Responses says "in exactly one shape" without qualification.
/// </para>
/// <para>
/// This is <b>not</b> the <c>Result</c>-to-<c>IActionResult</c> mapping that
/// <see href="../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
/// § Sub-decision 6</see> rules out doing in a filter. That decision is about
/// where the *mapping* lives — explicitly, at each endpoint, via
/// <c>ToActionResult()</c> — and this filter neither performs it nor competes
/// with it. It is a normaliser of last resort: on the sanctioned path it sees a
/// body that already carries <c>code</c> and leaves it untouched.
/// </para>
/// <para>
/// It runs as <see cref="IAlwaysRunResultFilter"/> so a short-circuiting filter
/// cannot route around it.
/// </para>
/// </remarks>
internal sealed class ProblemDetailsNormalizationFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Ours by construction, and it must be skipped before the value is
        // inspected: ProblemDetailsActionResult defers building its body until
        // ExecuteResultAsync, so at filter time its Value is still null and
        // rewriting it would throw away the `errors` map ValidationBehavior
        // and the model binder put there.
        if (context.Result is ProblemDetailsActionResult)
        {
            return;
        }

        if (context.Result is not ObjectResult { StatusCode: >= 400 } result)
        {
            return;
        }

        if (IsLearnStackProblem(result.Value))
        {
            return;
        }

        var problem = ProblemDetailsFactory.ForStatus(
            result.StatusCode!.Value, context.HttpContext);

        context.Result = new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { ProblemDetailsMediaType.Value },
        };
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
        // Nothing to do after the fact; the rewrite has to happen before the
        // body is serialised.
    }

    /// <summary>
    /// Ours carries <c>code</c>. ASP.NET's <see cref="ProblemDetails"/> does
    /// not — it carries <c>traceId</c> and an English <c>title</c> — so the
    /// extension is the discriminator, not the type.
    /// </summary>
    private static bool IsLearnStackProblem(object? value) =>
        value is ProblemDetails problem
        && problem.Extensions.ContainsKey("code");
}

/// <summary>
/// The one spelling of the Problem Details media type. Two spellings shipped —
/// one from <c>ContentTypes.Add</c>, one from <c>WriteAsJsonAsync</c>, which
/// appends <c>; charset=utf-8</c> — and made a routing 404 distinguishable from
/// an MVC 404, which
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// requires not to be.
/// </summary>
public static class ProblemDetailsMediaType
{
    /// <summary>
    /// The media type <b>with</b> its charset. Several spellings shipped —
    /// <c>WriteAsJsonAsync</c> appends <c>; charset=utf-8</c>, an
    /// <c>ObjectResult</c>'s <c>ContentTypes</c> does not — which made a
    /// routing 404 tellable from an MVC 404 without reading either body.
    /// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
    /// does not legislate media types; what it fixes is that a rejected
    /// assertion answers <b>404</b> so a caller cannot tell "wrong tenant" from
    /// "no such thing". A response header that distinguishes the two 404s
    /// hands back exactly the bit that decision withholds, which is why the
    /// spellings had to converge. The charset-bearing form is the one to
    /// converge on, because it is the one the middleware path cannot avoid
    /// emitting.
    /// </summary>
    public const string Value = "application/problem+json; charset=utf-8";

    /// <summary>
    /// The bare media type, for a caller that has to compare or negotiate on
    /// the type alone.
    /// </summary>
    public const string WithoutCharset = "application/problem+json";
}

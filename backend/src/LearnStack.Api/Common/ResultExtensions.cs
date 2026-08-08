using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Common;

/// <summary>
/// Maps a <see cref="Result{T}"/> to an <see cref="IActionResult"/> per
/// ADR-0032 § Sub-decision 6. The sanctioned shape — explicit at every
/// controller endpoint:
///
/// <code>
/// [HttpPost]
/// public async Task&lt;IActionResult&gt; Create(CreateCourseCommand cmd, CancellationToken ct)
///     =&gt; (await _mediator.Send(cmd, ct)).ToActionResult();
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// No action filter, no MediatR <c>ResultUnwrapBehavior</c>, no implicit
/// conversion. Explicit beats magic.
/// </para>
/// <para>
/// The failure path returns a <see cref="ProblemDetailsActionResult"/>
/// that defers <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/> assembly until
/// <c>ExecuteResultAsync</c>; that's when ASP.NET hands us the
/// <see cref="HttpContext"/> so <c>instance</c> and the
/// <c>correlationId</c> extension populate without the caller having to
/// thread the context through.
/// </para>
/// </remarks>
public static class ResultExtensions
{
    public static IActionResult ToActionResult<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            return new OkObjectResult(result.Value);
        }

        var error = result.Error
            ?? throw new InvalidOperationException(
                "Result.IsFailure but Error is null — Result<T>.Fail enforces a non-null Error.");

        return new ProblemDetailsActionResult(error);
    }
}

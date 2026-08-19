using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Common;

/// <summary>
/// <see cref="ObjectResult"/> subtype that defers the
/// <see cref="ProblemDetails"/> body construction until ASP.NET invokes
/// <see cref="ExecuteResultAsync"/>. The deferred build is what lets the
/// sanctioned controller shape per ADR-0032 § Sub-decision 6
/// (<c>(await _mediator.Send(cmd, ct)).ToActionResult()</c>) populate
/// <see cref="ProblemDetails.Instance"/> and the <c>correlationId</c>
/// extension from the current request without the caller having to thread
/// <see cref="Microsoft.AspNetCore.Http.HttpContext"/> explicitly.
/// </summary>
public sealed class ProblemDetailsActionResult : ObjectResult
{
    public ProblemDetailsActionResult(Error error)
        : base(value: null)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
        StatusCode = HttpStatusMap.For(error);
    }

    /// <summary>The carried failure — kept around for tests + extension points.</summary>
    public Error Error { get; }

    public override Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = ProblemDetailsFactory.For(Error, context.HttpContext);
        Value = problem;
        StatusCode = problem.Status;
        ContentTypes.Clear();
        ContentTypes.Add(ProblemDetailsMediaType.Value);
        return base.ExecuteResultAsync(context);
    }
}

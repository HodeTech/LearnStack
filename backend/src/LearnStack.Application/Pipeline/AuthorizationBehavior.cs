using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 5 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Launch retains this shell until P03-2 jointly
/// activates the staged scoped policy implementation and trusted denial evidence.
/// </summary>
/// <remarks>
/// <see cref="ScopedAuthorizationBehavior{TRequest,TResponse}"/> exercises the
/// real coordinator in an explicit proof composition at this same slot. The
/// common Application layer consumes SharedKernel policy/decision contracts;
/// ASP.NET requirements remain in the API. The eight-step order is unchanged.
/// </remarks>
public sealed class AuthorizationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // TODO(2026-10-27, @platform): P03-2 Step 5 activates the scoped
        // coordinator only together with its trusted denial-evidence owner.

        return next();
    }
}

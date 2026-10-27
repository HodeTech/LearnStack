using LearnStack.Application.Authorization;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 5 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2), with live policy admission and mandatory denial evidence.
/// </summary>
/// <remarks>
/// The common Application layer consumes SharedKernel policy/decision contracts;
/// ASP.NET requirements remain in the API. The eight-step order is unchanged.
/// </remarks>
public sealed class AuthorizationBehavior<TRequest, TResponse>(OperationAuthorizationCoordinator coordinator)
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

        return coordinator.ExecuteRequestAsync<TRequest, TResponse>(request, () => next(), cancellationToken);
    }
}

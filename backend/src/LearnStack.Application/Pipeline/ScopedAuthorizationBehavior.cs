using LearnStack.Application.Authorization;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>Staged slot-5 adapter; launch registration activates only together with trusted denial evidence.</summary>
public sealed class ScopedAuthorizationBehavior<TRequest, TResponse>(OperationAuthorizationCoordinator coordinator)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull where TResponse : IResultBase
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        return coordinator.ExecuteRequestAsync<TRequest, TResponse>(request, () => next(), cancellationToken);
    }
}

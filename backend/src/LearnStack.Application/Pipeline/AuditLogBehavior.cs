using LearnStack.Application.Audit;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline step 3: delegates classification, intent attribution, outcomes and
/// outermost reconciliation to the shared <see cref="AuditExecutionCoordinator"/>.
/// </summary>
/// <remarks>
/// It remains outside tenant admission, authorization and the transaction, preserving
/// ADR-0032's canonical pipeline order and ADR-0033's post-transaction reconciliation.
/// Direct owners enter the same coordinator before constructing their handlers.
/// </remarks>
public sealed class AuditLogBehavior<TRequest, TResponse>(AuditExecutionCoordinator coordinator)
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

        return coordinator.ExecuteAsync(
            typeof(TRequest), request as IProvisionsTenant, () => next(), cancellationToken);
    }
}

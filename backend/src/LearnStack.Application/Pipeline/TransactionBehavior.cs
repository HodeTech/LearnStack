using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.Application.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// Step 6 selects request mode; the shared coordinator owns announcement,
/// producer enrollment and transaction completion for every entry (ADR-0057).
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse>(TransactionExecutionCoordinator coordinator,
    IBackgroundJobRequestCatalog requests)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    /// <inheritdoc />
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        return coordinator.ExecuteRequestAsync(
            request, BackgroundJobExecutionAdmission.ModeFor(typeof(TRequest), requests),
            () => next(), cancellationToken);
    }
}

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
public sealed class TransactionBehavior<TRequest, TResponse>(TransactionExecutionCoordinator coordinator)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    private static readonly TransactionMode Mode = typeof(TRequest)
        .IsDefined(typeof(PublicSurfaceAttribute), inherit: false)
            ? TransactionMode.ReadOnly
            : TransactionMode.ReadWrite;

    /// <inheritdoc />
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        return coordinator.ExecuteAsync(
            request as IProvisionsTenant, Mode, typeof(TRequest).Name,
            () => next(), cancellationToken);
    }
}

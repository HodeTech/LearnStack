using LearnStack.Application.Messaging;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// Step 7 retains its canonical position as the producer enrollment/invariant guard.
/// Enqueue inserts immediately; owner completion drains domain events (ADR-0057).
/// This historical behavior name does not imply a buffered flush.
/// </summary>
public sealed class OutboxFlushBehavior<TRequest, TResponse>(ProducerExecution producer)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        producer.EnsureCurrentInvocation();
        var response = await next();
        producer.EnsureCurrentInvocation();
        return response;
    }
}

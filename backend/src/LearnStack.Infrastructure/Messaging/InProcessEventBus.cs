using System.Reflection;
using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>
/// The default <see cref="IEventBus"/>: a first-class transport, not a stub.
/// </summary>
/// <remarks>
/// <para>
/// It carries the same four obligations as the durable path, and each one is
/// here because a development transport that dropped it would be a development
/// path where the production behaviour is never exercised:
/// </para>
/// <list type="bullet">
/// <item>the same <see cref="IIntegrationEventHandler{TEvent}"/> contract, so no
/// consumer needs a second implementation and the one running in CI is the one
/// that runs in production;</item>
/// <item>the same consumer-side deduplication — the handler calls
/// <c>IInboxGuard</c> itself, exactly as it does behind a broker, because a
/// transport that never delivers a duplicate never surfaces the most common
/// integration-event defect;</item>
/// <item>the same tenant-context restoration from the event, so Row Level
/// Security and the query filters are exercised on the consumer side;</item>
/// <item>the same per-partition-key ordering, because an ordering assumption
/// that holds only in one process is discovered in production.</item>
/// </list>
/// <para>
/// What it genuinely does not provide — and therefore the trigger for the Dapr
/// adapter in
/// <see href="../../../../docs/roadmap/phase-11-production-hardening.md">Phase 11</see>
/// per <see href="../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>
/// — is delivery to a second process, broker-side retention and replay.
/// </para>
/// </remarks>
public sealed class InProcessEventBus(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantAccessor,
    IPartitionSerializer partitions) : IEventBus
{
    private const string HandleMethodName =
        nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync);

    public Task PublishAsync(
        IIntegrationEvent @event,
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        return partitions.RunSequentiallyFor(partitionKey, async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            // Restored into the flow the handler runs in, and the publisher's own
            // context put back afterwards — a synchronous dispatch would
            // otherwise leak a tenant into the caller that published the event.
            var previous = tenantAccessor.Current;
            tenantAccessor.Current = EventTenantContext.FromEvent(@event, previous?.CorrelationId);

            try
            {
                // By RUNTIME type. `@event` is declared as the base interface
                // here, so a closed generic over its static type would resolve
                // IIntegrationEventHandler<IIntegrationEvent> — which no concrete
                // consumer implements — and the publish would reach zero handlers
                // and report success.
                var contract = typeof(IIntegrationEventHandler<>).MakeGenericType(@event.GetType());

                // Invoked through the INTERFACE method rather than by `dynamic`
                // on the instance. The dynamic binder honours accessibility, so
                // an `internal` handler — the normal shape for a module's own
                // consumer — would fail to bind at runtime; an interface method
                // dispatches virtually and does not care what the concrete type's
                // visibility is.
                var handle = contract.GetMethod(HandleMethodName)!;

                foreach (var handler in scope.ServiceProvider.GetServices(contract))
                {
                    Task delivery;

                    try
                    {
                        delivery = (Task)handle.Invoke(handler, [@event, cancellationToken])!;
                    }
                    catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
                    {
                        // A handler that throws before its first await throws out
                        // of Invoke, which wraps it. Unwrapped and rethrown with
                        // its stack intact, because a consumer and the error
                        // pipeline both key on the exception type: a
                        // TargetInvocationException would tell them the transport
                        // failed when the handler did.
                        ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
                        throw;
                    }

                    await delivery;
                }
            }
            finally
            {
                tenantAccessor.Current = previous;
            }
        });
    }
}

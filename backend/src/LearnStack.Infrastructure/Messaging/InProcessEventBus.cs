using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>The process-local <see cref="IEventBus"/> transport.</summary>
/// <remarks>
/// It preserves the durable transport's handler contract, tenant restoration,
/// per-partition ordering and per-subscription failure isolation. Each
/// subscription is constructed once in its own async scope. Dapr becomes the
/// adapter when ADR-0035's multi-process/replay trigger is met.
/// </remarks>
public sealed partial class InProcessEventBus(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantAccessor,
    IPartitionSerializer partitions,
    IntegrationEventHandlerRegistry handlers,
    ILogger<InProcessEventBus> logger) : IEventBus
{
    public const string ActivitySourceName = "learnstack.messaging";

    private const string HandleMethodName =
        nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync);

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public Task PublishAsync(
        IntegrationEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return partitions.RunSequentiallyFor(
            envelope.PartitionKey,
            () => DispatchAsync(envelope, cancellationToken));
    }

    private async Task DispatchAsync(
        IntegrationEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var previous = tenantAccessor.Current;

        // Established before even subscription lookup. Any future registry or
        // resolution work that observes ITenantContext therefore sees the event,
        // never the publisher that happened to invoke this transport.
        tenantAccessor.Current = EventTenantContext.FromEnvelope(envelope);

        try
        {
            var subscriptions = handlers.For(envelope.Event.GetType());
            if (subscriptions.Count == 0)
            {
                ReachedNoHandler(logger, envelope.Event.GetType().Name, envelope.Event.EventId);
                return;
            }

            _ = ActivityContext.TryParse(
                envelope.CorrelationId,
                traceState: null,
                out var parentContext);

            List<Exception>? failures = null;

            for (var index = 0; index < subscriptions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subscription = subscriptions[index];

                try
                {
                    await DeliverAsync(
                            subscription,
                            envelope,
                            parentContext,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Publish cancellation is control flow, not a poison
                    // subscription. Stop immediately so later handlers do not
                    // start business work during shutdown.
                    throw;
                }
                catch (Exception exception)
                {
                    HandlerFailed(
                        logger,
                        envelope.Event.GetType().Name,
                        envelope.Event.EventId,
                        subscription.HandlerType.Name,
                        envelope.Event.TenantId,
                        envelope.PartitionKey,
                        exception);
                    (failures ??= []).Add(exception);
                }
            }

            if (failures is { Count: 1 })
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    $"{failures.Count} handlers failed for {envelope.Event.GetType().Name}.",
                    failures);
            }
        }
        finally
        {
            tenantAccessor.Current = previous;
        }
    }

    private async Task DeliverAsync(
        IntegrationEventSubscription subscription,
        IntegrationEventEnvelope envelope,
        ActivityContext parentContext,
        CancellationToken cancellationToken)
    {
        var previous = tenantAccessor.Current;
        tenantAccessor.Current = EventTenantContext.FromEnvelope(envelope, subscription.ModuleName);

        try
        {
            using var activity = ActivitySource.StartActivity(
                $"{envelope.Topic} process",
                ActivityKind.Consumer,
                parentContext,
                tags:
                [
                    new("messaging.system", "in-process"),
                    new("messaging.destination.name", envelope.Topic),
                    new("messaging.operation.type", "process"),
                    new("learnstack.module", subscription.ModuleName),
                ]);

            await using var scope = scopeFactory.CreateAsyncScope();

            object handler;
            try
            {
                handler = scope.ServiceProvider.GetRequiredService(subscription.HandlerType);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Integration-event handler {subscription.HandlerType.FullName} "
                    + "failed to construct.",
                    exception);
            }

            var handle = subscription.Handle;
            Task delivery;

            try
            {
                delivery = (Task)handle.Invoke(handler, [envelope.Event, cancellationToken])!;
            }
            catch (TargetInvocationException wrapped)
                when (wrapped.InnerException is OperationCanceledException cancellation
                      && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "An integration-event handler was cancelled by a token other than "
                    + "the publish token.",
                    cancellation);
            }
            catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
                throw;
            }

            if (delivery is null)
            {
                throw new InvalidOperationException(
                    $"{handler.GetType().FullName} returned a null Task from {HandleMethodName}.");
            }

            try
            {
                await delivery.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "An integration-event handler was cancelled by a token other than "
                    + "the publish token.",
                    exception);
            }
        }
        finally
        {
            tenantAccessor.Current = previous;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Integration event {EventType} ({IntegrationEventId}) failed in handler "
            + "{HandlerType} for tenant {TenantId} on partition {PartitionKey}")]
    private static partial void HandlerFailed(
        ILogger logger,
        string eventType,
        Guid integrationEventId,
        string handlerType,
        Guid tenantId,
        string partitionKey,
        Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Integration event {EventType} ({IntegrationEventId}) reached no handler")]
    private static partial void ReachedNoHandler(
        ILogger logger,
        string eventType,
        Guid integrationEventId);
}

using System.Reflection;
using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// integration-event defect. That seam lands in Phase 02b; today the contract is
/// shaped for it and nothing else;</item>
/// <item>the same tenant-context restoration, into the scope the handler
/// resolves from, so Row Level Security and the query filters are exercised on
/// the consumer side;</item>
/// <item>the same per-partition-key ordering, because an ordering assumption
/// that holds only in one process is discovered in production.</item>
/// </list>
/// <para>
/// It also carries the same <b>failure isolation</b>. Poison-message containment
/// is per subscription: one module's broken handler must not deny another module
/// the event. Every handler is attempted, and the failures are reported
/// together.
/// </para>
/// <para>
/// What it genuinely does not provide — and therefore the trigger for the Dapr
/// adapter in
/// <see href="../../../../docs/roadmap/phase-11-production-hardening.md">Phase 11</see>
/// per <see href="../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>
/// — is delivery to a second process, broker-side retention and replay.
/// </para>
/// </remarks>
public sealed partial class InProcessEventBus(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantAccessor,
    IPartitionSerializer partitions,
    ILogger<InProcessEventBus> logger) : IEventBus
{
    private const string HandleMethodName =
        nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync);

    public Task PublishAsync(
        IntegrationEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // Checked before anything is queued, so a publish on an already-cancelled
        // token behaves the way a broker-backed one would — it fails rather than
        // dispatching. Returned as a faulted task rather than thrown inline: a
        // fire-and-forget call site must not crash its caller synchronously.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return partitions.RunSequentiallyFor(
            envelope.PartitionKey,
            () => DispatchAsync(envelope, cancellationToken));
    }

    private async Task DispatchAsync(
        IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        // By RUNTIME type. The event is declared as the base interface here, so a
        // closed generic over its static type would resolve
        // IIntegrationEventHandler<IIntegrationEvent> — which no concrete
        // consumer implements — and the publish would reach zero handlers and
        // report success.
        var contract = typeof(IIntegrationEventHandler<>)
            .MakeGenericType(envelope.Event.GetType());
        var handle = contract.GetMethod(HandleMethodName)!;
        var context = EventTenantContext.FromEnvelope(envelope);
        var count = HandlerCount(contract, envelope, out var constructionFailure);

        if (constructionFailure is not null)
        {
            // A handler whose CONSTRUCTOR throws takes every sibling with it,
            // and there is nothing this class can do about that: the container
            // materialises the whole array before returning any element, so the
            // failure lands before the per-handler loop that provides isolation
            // can start. Measured — a healthy handler registered alongside a
            // throwing one never had HandleAsync called at all. It is said
            // plainly here rather than surfacing as a bare constructor exception
            // from a transport the caller did not know it was in.
            throw new InvalidOperationException(
                $"An integration-event handler for {envelope.Event.GetType().Name} failed to "
                + "construct, so no handler for that event could run. Handler construction "
                + "happens before per-handler isolation and cannot be contained.",
                constructionFailure);
        }

        if (count == 0)
        {
            // Not an error — an event nobody consumes is legitimate — but silence
            // here is indistinguishable from a handler registered for a type the
            // container will never match, so it is said out loud once.
            ReachedNoHandler(logger, envelope.Event.GetType().Name, envelope.Event.EventId);
            return;
        }

        List<Exception>? failures = null;

        for (var index = 0; index < count; index++)
        {
            try
            {
                await DeliverAsync(contract, handle, index, envelope, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Collected, not rethrown here. Poison-message containment is per
                // subscription: letting the first fault escape the loop would let
                // one module's broken handler deny every other module the event,
                // with no retry and no dead-letter to show for it.
                HandlerFailed(
                    logger,
                    envelope.Event.GetType().Name,
                    envelope.Event.EventId,
                    index,
                    envelope.Event.TenantId,
                    envelope.PartitionKey,
                    ex);

                (failures ??= []).Add(ex);
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

    private async Task DeliverAsync(
        Type contract,
        MethodInfo handle,
        int index,
        IntegrationEventEnvelope envelope,
        ITenantContext context,
        CancellationToken cancellationToken)
    {
        // One scope per HANDLER, not one per event. Under a broker each
        // subscription gets its own; sharing one here would hand two modules'
        // consumers the same DbContext and the same unit of work, so a failed
        // handler's dirty state would cross a module boundary the architecture
        // otherwise enforces hard.
        //
        // Selected by index rather than by concrete type, because a handler is
        // registered against the CONTRACT — its own type is not a service, and
        // asking the container for it fails.
        //
        // The cost, stated plainly: the container materialises the whole array
        // for each scope, so N handlers for one event means N constructions per
        // scope and N scopes — measured, twelve constructions for three
        // handlers. Only one HandleAsync runs per handler, so business logic is
        // never duplicated; what repeats is construction. That is affordable
        // exactly as long as a handler's constructor does nothing but assign
        // fields — which is the DI convention anyway, and is now a requirement
        // rather than a habit. A constructor that opens a connection, emits a
        // metric or writes a log line will do it N+1 times per delivery.
        await using var scope = scopeFactory.CreateAsyncScope();

        // Restored into the flow the handler runs in AND into the scope it
        // resolves ITenantContext from — the composition root binds the scoped
        // context to this accessor. Setting only the ambient one left the scoped
        // ITenantContext unresolved, so a handler injecting it threw and a
        // handler sending a MediatR command was short-circuited by
        // TenantContextBehavior before its business logic ran.
        var previous = tenantAccessor.Current;
        tenantAccessor.Current = context;

        try
        {
            var handler = scope.ServiceProvider.GetServices(contract).ElementAt(index)!;

            Task delivery;

            try
            {
                delivery = (Task)handle.Invoke(handler, [envelope.Event, cancellationToken])!;
            }
            catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
            {
                // A handler that throws before its first await throws out of
                // Invoke, which wraps it. Unwrapped and rethrown with its stack
                // intact, because a consumer and the error pipeline both key on
                // the exception type: a TargetInvocationException would tell them
                // the transport failed when the handler did.
                ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
                throw;
            }

            if (delivery is null)
            {
                throw new InvalidOperationException(
                    $"{handler.GetType().FullName} returned a null Task from {HandleMethodName}.");
            }

            await delivery.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A handler that observed some OTHER cancellation must not make the
            // publish look cancelled: an outbox processor would read that as
            // "we are shutting down, retry later" and silently swallow a handler
            // that ran and gave up.
            throw new InvalidOperationException(
                "An integration-event handler was cancelled by a token other than the publish token.",
                ex);
        }
        finally
        {
            tenantAccessor.Current = previous;
        }
    }

    private int HandlerCount(
        Type contract, IntegrationEventEnvelope envelope, out Exception? constructionFailure)
    {
        constructionFailure = null;

        using var scope = scopeFactory.CreateScope();

        try
        {
            return scope.ServiceProvider.GetServices(contract).Count();
        }
        catch (Exception ex)
        {
            HandlerConstructionFailed(
                logger, envelope.Event.GetType().Name, envelope.Event.EventId, ex);
            constructionFailure = ex;
            return 0;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Integration event {EventType} ({IntegrationEventId}) failed in handler "
            + "#{HandlerIndex} for tenant {TenantId} on partition {PartitionKey}")]
    private static partial void HandlerFailed(
        ILogger logger,
        string eventType,
        Guid integrationEventId,
        int handlerIndex,
        Guid tenantId,
        string partitionKey,
        Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "A handler for integration event {EventType} ({IntegrationEventId}) failed "
            + "to construct; no handler for that event ran")]
    private static partial void HandlerConstructionFailed(
        ILogger logger, string eventType, Guid integrationEventId, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Integration event {EventType} ({IntegrationEventId}) reached no handler")]
    private static partial void ReachedNoHandler(
        ILogger logger, string eventType, Guid integrationEventId);

}

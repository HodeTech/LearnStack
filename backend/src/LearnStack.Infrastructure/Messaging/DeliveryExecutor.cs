using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using LearnStack.Application.Audit;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Owns one application scope through actual delivery execution and cleanup.</summary>
public sealed class DeliveryExecutor(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantAccessor,
    IPartitionSerializer partitions,
    OutboxEventRegistry registry,
    IntegrationEventHandlerRegistry handlers)
{
    private static readonly ActivitySource Activities = new(InProcessEventBus.ActivitySourceName);

    /// <summary>
    /// The caller may stop waiting, close the gate and cancel the callback, but
    /// must retain this task until completion: it still owns the live scope.
    /// </summary>
    public async Task<DeliveryExecutionResult> ExecuteAsync(
        DeliveryClaim claim, DeliveryCommitGate gate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(gate);
        if (!gate.TryStartExecution())
            throw new InvalidOperationException("A delivery commit gate belongs to exactly one execution.");
        if (!ValidClaim(claim))
            return new(DeliveryExecutionOutcome.InvalidEnvelope, false, false);

        // The registration touches only the atomic gate, never a live connection.
        using var registration = cancellationToken.Register(static state => ((DeliveryCommitGate)state!).Close(), gate);
        DeliveryExecutionResult result = new(DeliveryExecutionOutcome.Failed, false, false);
        await partitions.RunSequentiallyFor(claim.PartitionKey, async () =>
        {
            result = await ExecuteOwnedAsync(claim, gate, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result;
    }

    private async Task<DeliveryExecutionResult> ExecuteOwnedAsync(
        DeliveryClaim claim, DeliveryCommitGate gate, CancellationToken cancellationToken)
    {
        var outcome = DeliveryExecutionOutcome.Failed;
        var cleanupFailed = false;
        var previous = tenantAccessor.Current;
        AsyncServiceScope? scope = null;
        TransactionExecutionCoordinator.PreparedDeliveryOwner? prepared = null;
        IUnitOfWork? unit = null;
        Activity? activity = null;
        try
        {
            // Routing validation/read owns no inherited caller identity. Module
            // services see only the fully validated event context installed below.
            tenantAccessor.Current = null;
            cancellationToken.ThrowIfCancellationRequested();
            // Before retained-envelope validation resolve only the bare unit.
            // The routing context is exclusively a SQL setter argument; it is
            // never installed in the accessor or captured by a module service.
            scope = scopeFactory.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            prepared = await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(
                unit, new DeliveryRoutingContext(claim), cancellationToken).ConfigureAwait(false);
            var completion = new PostgresDeliveryCompletion(unit, gate, claim);
            var stored = await completion.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                outcome = DeliveryExecutionOutcome.LostClaim;
            }
            else if (!MatchesClaim(stored, claim))
            {
                outcome = DeliveryExecutionOutcome.InvalidEnvelope;
            }
            else
            {
                var resolution = registry.Resolve(stored.Type);
                var envelope = StoredEventEnvelopeReader.Read(stored, resolution);
                var subscription = SelectSubscription(resolution, claim.SubscriptionId);
                if (envelope is null)
                {
                    outcome = DeliveryExecutionOutcome.InvalidEnvelope;
                }
                else if (subscription is null)
                {
                    outcome = DeliveryExecutionOutcome.InvalidSubscription;
                }
                else
                {
                    // Install in this async method and retain through owned
                    // disposal. AsyncLocal writes in an async helper would not
                    // propagate back to this scope's subsequent resolutions.
                    tenantAccessor.Current = EventTenantContext.FromEnvelope(envelope, subscription.ModuleName);
                    activity = BeginConsumerActivity(envelope);
                    var services = scope.Value.ServiceProvider;
                    var inbox = services.GetRequiredService<IInboxGuard>();
                    var claimed = await inbox.TryClaimAsync(
                        TenantId.From(claim.TenantId), claim.SubscriptionId, claim.EventId,
                        cancellationToken).ConfigureAwait(false);
                    var transaction = services.GetRequiredService<TransactionExecutionCoordinator>();
                    outcome = claimed ? DeliveryExecutionOutcome.Completed : DeliveryExecutionOutcome.Duplicate;
                    if (claimed)
                    {
                        var audit = services.GetRequiredService<AuditExecutionCoordinator>();
                        var response = await audit.ExecuteAsync(subscription.HandlerType, null,
                            () => transaction.ExecutePreparedDeliveryAsync(prepared, completion, claim.EventId,
                                () => InvokeAsync(services, subscription, envelope.Event, cancellationToken),
                                cancellationToken), cancellationToken).ConfigureAwait(false);
                        if (response.IsFailure) outcome = DeliveryExecutionOutcome.Refused;
                    }
                    else
                    {
                        // A durable inbox duplicate bypasses classification and
                        // construction, but still completes this claim under its fence.
                        await transaction.ExecutePreparedDeliveryAsync(prepared, completion, claim.EventId,
                            () => Task.FromResult(Result<None>.Ok(None.Value)), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (gate.CommitAcknowledged)
            {
                cleanupFailed = true;
            }
            else
            {
                outcome = gate.CommitStarted && unit?.IsRollbackOnly != true
                    ? DeliveryExecutionOutcome.Indeterminate
                    : exception is OperationCanceledException && (cancellationToken.IsCancellationRequested || gate.IsClosed)
                        ? DeliveryExecutionOutcome.Canceled : DeliveryExecutionOutcome.Failed;
            }
            // No exception messages, raw types, stacks or payloads cross this boundary.
        }
        finally
        {
            try
            {
                if (prepared is not null) await prepared.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                cleanupFailed = true;
            }

            try
            {
                if (scope is { } ownedScope) await ownedScope.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                cleanupFailed = true;
            }
            finally
            {
                try
                {
                    activity?.Dispose();
                }
                catch
                {
                    cleanupFailed = true;
                }
                finally
                {
                    tenantAccessor.Current = previous;
                }
            }
        }

        return new(outcome, gate.CommitAcknowledged, cleanupFailed);
    }

    private IntegrationEventSubscription? SelectSubscription(OutboxEventResolution resolution, string id)
    {
        var selected = resolution.Subscriptions.SingleOrDefault(subscription => subscription.Id == id);
        if (selected is not
            {
                Readiness: OutboxSubscriptionReadiness.Active,
                ExecutionScope: OutboxSubscriptionExecutionScope.OrdinaryTenant, HandlerType: not null
            }
            || resolution.EventType is null)
            return null;
        // A Pending sibling holds producer acquisition, not an already accepted
        // Active delivery. Exact manifest and handler identity remain mandatory.
        return handlers.For(resolution.EventType).SingleOrDefault(
            subscription => subscription.HandlerType == selected.HandlerType);
    }

    private static async Task<Result<None>> InvokeAsync(
        IServiceProvider services, IntegrationEventSubscription subscription,
        IIntegrationEvent fact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handler = services.GetRequiredService(subscription.HandlerType);
        Task task;
        try
        {
            task = subscription.Handle.Invoke(handler, [fact, cancellationToken]) as Task
                ?? throw new InvalidOperationException("An integration handler must return its execution task.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        await task.ConfigureAwait(false);
        return Result<None>.Ok(None.Value);
    }

    private static Activity BeginConsumerActivity(IntegrationEventEnvelope envelope)
    {
        _ = ActivityContext.TryParse(envelope.CorrelationId, null, isRemote: true, out var parent);
        return Activities.StartActivity("delivery.execute", ActivityKind.Consumer, parent)
            ?? new Activity("delivery.execute").SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start();
    }

    private static bool MatchesClaim(StoredEventEnvelope stored, DeliveryClaim claim) =>
        stored.EventId == claim.EventId && stored.TenantId == claim.TenantId
        && stored.OrganizationId == claim.OrganizationId && stored.Type == claim.Type
        && stored.Topic == claim.Topic && stored.PartitionKey == claim.PartitionKey
        && stored.OccurredAt.UtcTicks / 10 == claim.OccurredAt.UtcTicks / 10;

    private static bool ValidClaim(DeliveryClaim claim) =>
        claim.TenantId != Guid.Empty && claim.TenantId != TenantId.PlatformSentinel.Value
        && claim.EventId != Guid.Empty && claim.OrganizationId != Guid.Empty
        && OutboxEventRegistry.ValidSubscriptionId(claim.SubscriptionId)
        && Guid.TryParseExact(claim.Token, "D", out var token) && token != Guid.Empty
        && claim.Token == token.ToString("D") && !string.IsNullOrWhiteSpace(claim.PartitionKey)
        && claim.Attempts is >= 1 and <= 6 && claim.Kind == DeliveryClaimKind.Normal;

    private sealed class DeliveryRoutingContext(DeliveryClaim claim) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId { get; } = TenantId.From(claim.TenantId);
        public OrganizationId? OrganizationId { get; } = claim.OrganizationId is { } id
            ? LearnStack.SharedKernel.Identifiers.OrganizationId.From(id) : null;
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => null;

    }
}

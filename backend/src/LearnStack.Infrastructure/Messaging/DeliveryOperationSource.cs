using System.Data.Common;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Installed only by the retained, validated delivery owner; policy metadata supplies no owner.</summary>
internal sealed class DeliveryOperationSource(IOperationPolicyCatalog policies, IUnitOfWork unit,
    ITenantContextAccessor accessor) : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
{
    private Frame? _frame;

    internal IDisposable Enter(TransactionExecutionCoordinator.PreparedDeliveryOwner prepared,
        IntegrationEventSubscription subscription, IntegrationEventEnvelope envelope,
        DeliveryCommitGate gate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(gate);
        if (!CanIssue || _frame is not null || accessor.Current is not { } context
            || unit.Transaction is not { } transaction
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announcement
            || subscription.EventType != envelope.Event.GetType()
            || envelope.Event.EventId == Guid.Empty || envelope.Event.TenantId != announcement.TenantId.Value
            || envelope.OrganizationId != announcement.OrganizationId?.Value)
            throw new InvalidOperationException("Delivery operation provenance is invalid.");
        var frame = new Frame(prepared, subscription, envelope.Event.EventId, context, transaction,
            announcement, gate, cancellationToken);
        _frame = frame;
        if (!IsEvidenceCurrent(frame))
        {
            frame.Close();
            throw new InvalidOperationException("Delivery operation provenance is invalid.");
        }
        return new Scope(frame);
    }

    public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(context);
        if (!CanIssue || _frame is not { } frame || !ReferenceEquals(frame.Context, context)
            || !IsEvidenceCurrent(frame) || !policies.TryGet(requestType, out var policy)
            || policy.Kind != OperationPolicyKind.FiniteTrusted
            || !policy.AllowedSources.Contains(AuthorizationSourceKind.IntegrationEvent)
            || policy.TrustedEventHandlerType != frame.Subscription.HandlerType) return null;
        return ForOperation(AuthorizationSourceKind.IntegrationEvent, requestType, context, frame);
    }

    protected override bool IsEvidenceCurrent(object? evidence) => evidence is Frame frame
        && ReferenceEquals(frame, _frame) && frame.IsOpen && !frame.CancellationToken.IsCancellationRequested
        && !frame.Gate.IsClosed && !frame.Gate.CommitStarted && !frame.Gate.CommitAcknowledged
        && ReferenceEquals(accessor.Current, frame.Context)
        && unit.HasActiveTransaction && !unit.IsRollbackOnly && unit.Mode == TransactionMode.ReadWrite
        && ReferenceEquals(unit.Transaction, frame.Transaction)
        && unit.IsTenantContextIssuedOn(frame.Transaction)
        && unit.GetTenantContextAnnouncement(frame.Transaction) == frame.Announcement
        && frame.Context.IsResolved && frame.Context.Origin == TenantContextOrigin.Ambient
        && frame.Context.HostScope is null && frame.Context.UserId == UserId.SystemActor
        && frame.Context.TenantId == frame.Announcement.TenantId
        && frame.Context.OrganizationId == frame.Announcement.OrganizationId;

    private sealed class Frame(TransactionExecutionCoordinator.PreparedDeliveryOwner prepared,
        IntegrationEventSubscription subscription, Guid eventId, ITenantContext context,
        DbTransaction transaction, TenantContextAnnouncement announcement,
        DeliveryCommitGate gate, CancellationToken cancellationToken)
    {
        private int _closed;
        internal TransactionExecutionCoordinator.PreparedDeliveryOwner Prepared { get; } = prepared;
        internal IntegrationEventSubscription Subscription { get; } = subscription;
        internal Guid EventId { get; } = eventId;
        internal ITenantContext Context { get; } = context;
        internal DbTransaction Transaction { get; } = transaction;
        internal TenantContextAnnouncement Announcement { get; } = announcement;
        internal DeliveryCommitGate Gate { get; } = gate;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal bool IsOpen => Volatile.Read(ref _closed) == 0;
        internal void Close() => Interlocked.Exchange(ref _closed, 1);
        public override string ToString() => nameof(Frame);
    }

    private sealed class Scope(Frame frame) : IDisposable
    {
        public void Dispose() => frame.Close();
    }
}

/// <summary>Registers the confined source without activating application authorization.</summary>
public static class DeliveryOperationSourceRegistration
{
    public static IServiceCollection AddTrustedDeliveryOperations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ITrustedOperationSource, DeliveryOperationSource>());
        return services;
    }
}

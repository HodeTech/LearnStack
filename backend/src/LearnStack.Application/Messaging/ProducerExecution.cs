using System.Diagnostics;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Application.Messaging;

/// <summary>
/// Scoped producer ancestry and sticky successful-production receipts (ADR-0057).
/// One logical execution uses a scope; nested invocations run sequentially.
/// </summary>
public sealed class ProducerExecution(IUnitOfWork unitOfWork)
{
    private Invocation? _current;

    /// <summary>Whether an invocation is open, independently of tenant admission.</summary>
    public bool HasInvocation => _current is not null;

    /// <summary>
    /// Enrolls after announcement. An unresolved, event-free invocation is legal;
    /// actual production must pass <see cref="RequireAnnouncement"/>.
    /// </summary>
    internal Invocation BeginInvocation(bool isTransactionOwner, UserId? causalActor = null)
    {
        if (isTransactionOwner && _current is not null)
        {
            throw new InvalidOperationException("A producer owner is already active in this scope.");
        }

        var announcement = unitOfWork.GetTenantContextAnnouncement(unitOfWork.Transaction);
        // An orphan UoW joiner is not a producer owner. It can finish event-free work,
        // but cannot enlist production on a raw transaction nobody will complete here.
        var hasOwner = isTransactionOwner
            || (_current is { HasOwner: true, Announcement: not null } parent
                && parent.Announcement == announcement);
        // A retained invocation cannot lend ownership to a later raw transaction,
        // even when Npgsql reuses the transaction object and the tenant is unchanged.
        Activity? ownedActivity = null;
        if (Activity.Current is not { IdFormat: ActivityIdFormat.W3C, Id: not null })
        {
            // Synchronous in the caller's execution context. ActivitySource alone returns
            // null when the Seeder has no telemetry listener.
            ownedActivity = new Activity("learnstack.producer")
                .SetIdFormat(ActivityIdFormat.W3C).Start();
        }

        var invocation = new Invocation(this, _current, announcement, hasOwner, ownedActivity,
            _current is null ? causalActor : _current.CausalActor);
        _current = invocation;
        return invocation;
    }

    /// <summary>
    /// Returns live authority for the enrolled, writable, unpoisoned frame only.
    /// Retained snapshots and reused transaction objects cannot authorize a new unit.
    /// </summary>
    public TenantContextAnnouncement RequireAnnouncement()
    {
        var current = _current;
        var live = unitOfWork.GetTenantContextAnnouncement(unitOfWork.Transaction);
        if (current is null || !current.HasOwner || current.Announcement is null
            || live is null || live != current.Announcement
            || !unitOfWork.HasActiveTransaction || unitOfWork.Mode != TransactionMode.ReadWrite
            || unitOfWork.IsRollbackOnly)
        {
            throw new InvalidOperationException(
                "Production requires an enrolled writable frame with its live tenant announcement.");
        }

        return live;
    }

    /// <summary>Refuses a store paired with another unit, even if announcements match.</summary>
    public TenantContextAnnouncement RequireAnnouncement(IUnitOfWork expectedUnit)
    {
        if (!ReferenceEquals(unitOfWork, expectedUnit))
        {
            throw new InvalidOperationException("The outbox must share its producer's unit of work.");
        }

        return RequireAnnouncement();
    }

    /// <summary>
    /// Builds dispatch metadata from the admitted frame, never producer arguments.
    /// Organization authority comes from the SQL announcement even for a tenant-wide fact.
    /// </summary>
    public IntegrationEventEnvelope CreateEnvelope(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var announcement = RequireAnnouncement();
        if (integrationEvent.TenantId == Guid.Empty
            || integrationEvent.TenantId == TenantId.PlatformSentinel.Value
            || integrationEvent.TenantId != announcement.TenantId.Value)
        {
            throw new InvalidOperationException("The event tenant must match the live announcement.");
        }

        var traceparent = Activity.Current?.Id;
        if (traceparent is null)
        {
            throw new InvalidOperationException("An enrolled producer requires a W3C Activity.");
        }

        return new IntegrationEventEnvelope(integrationEvent, traceparent,
            announcement.OrganizationId?.Value, ActorUserId: _current!.CausalActor);
    }

    /// <summary>Step 7 checks enrollment and authority without requiring a produced event.</summary>
    internal void EnsureCurrentInvocation()
    {
        if (_current is null || !unitOfWork.HasActiveTransaction
            || _current.Announcement != unitOfWork.GetTenantContextAnnouncement(unitOfWork.Transaction))
        {
            throw new InvalidOperationException("The producer invocation no longer matches its transaction.");
        }
    }

    /// <summary>
    /// Called after successful outbox INSERT or successful-save promotion only.
    /// Receipts survive child disposal and queue drain; they are not pending counts.
    /// </summary>
    public void RecordProducedWork()
    {
        _ = RequireAnnouncement();
        for (var invocation = _current; invocation is not null; invocation = invocation.Parent)
        {
            invocation.HasProducedWork = true;
        }
    }

    /// <summary>One invocation, retained by its coordinator through owner completion.</summary>
    public sealed class Invocation : IDisposable
    {
        private readonly ProducerExecution _execution;
        private readonly Activity? _ownedActivity;
        private bool _disposed;

        internal Invocation(
            ProducerExecution execution, Invocation? parent,
            TenantContextAnnouncement? announcement, bool hasOwner, Activity? ownedActivity, UserId? causalActor)
        {
            _execution = execution;
            Parent = parent;
            Announcement = announcement;
            HasOwner = hasOwner;
            _ownedActivity = ownedActivity;
            CausalActor = causalActor;
        }

        internal Invocation? Parent { get; }
        internal TenantContextAnnouncement? Announcement { get; }
        internal bool HasOwner { get; }
        internal UserId? CausalActor { get; }

        /// <summary>Successful production by this invocation or any descendant.</summary>
        public bool HasProducedWork { get; internal set; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            if (!ReferenceEquals(_execution._current, this))
            {
                throw new InvalidOperationException("Producer invocations must close in LIFO order.");
            }

            _disposed = true;
            _execution._current = Parent;
            _ownedActivity?.Dispose();
        }
    }
}

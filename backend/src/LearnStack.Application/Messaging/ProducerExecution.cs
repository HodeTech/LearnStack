using System.Diagnostics;
using System.Data.Common;
using LearnStack.SharedKernel.Jobs;
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
    private DomainDispatch? _dispatch;

    /// <summary>Whether an invocation is open, independently of tenant admission.</summary>
    public bool HasInvocation => _current is not null;

    /// <summary>
    /// Enrolls after announcement. An unresolved, event-free invocation is legal;
    /// actual production must pass <see cref="RequireAnnouncement"/>.
    /// </summary>
    internal Invocation BeginInvocation(bool isTransactionOwner, UserId? causalActor = null,
        Guid? incomingEventId = null, Type? operationType = null, BackgroundJobId? parentJobId = null)
    {
        if (_dispatch is not null)
        {
            Poison();
            throw new InvalidOperationException("Domain translation cannot open another producer invocation.");
        }

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

        var invocation = new Invocation(this, _current, announcement, hasOwner, isTransactionOwner, ownedActivity,
            _current is null ? causalActor : _current.CausalActor,
            _current is null ? incomingEventId : _current.IncomingEventId, operationType,
            _current is null ? parentJobId : _current.ParentJobId);
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

        var traceparent = _dispatch?.Origin.Traceparent ?? Activity.Current?.Id;
        if (traceparent is null || !ActivityContext.TryParse(traceparent, null, out _))
        {
            throw new InvalidOperationException("An enrolled producer requires a W3C Activity.");
        }

        return new IntegrationEventEnvelope(integrationEvent, traceparent,
            announcement.OrganizationId?.Value, CausationId: _dispatch?.EventId ?? _current!.IncomingEventId,
            ActorUserId: _dispatch is null ? _current!.CausalActor : _dispatch.Origin.CausalActor);
    }

    /// <summary>Captures the exact trusted producer operation and live physical transaction.</summary>
    public JobOrigin CaptureJobOrigin(IUnitOfWork expectedUnit)
    {
        var announcement = RequireAnnouncement(expectedUnit);
        var invocation = _dispatch?.Origin.Invocation ?? _current!;
        var operation = invocation.OperationType
            ?? throw new InvalidOperationException("Job submission requires a trusted typed operation.");
        var traceparent = _dispatch?.Origin.Traceparent ?? Activity.Current?.Id;
        if (traceparent is null || !ActivityContext.TryParse(traceparent, null, out _))
            throw new InvalidOperationException("Job submission requires a W3C Activity.");
        return new JobOrigin(this, _current!, invocation, _dispatch, unitOfWork.Transaction!, operation,
            announcement, traceparent, _dispatch?.Origin.CausalActor ?? invocation.CausalActor,
            _dispatch?.EventId ?? invocation.IncomingEventId, invocation.ParentJobId);
    }

    /// <summary>Revalidates after arbitrary serialization and before the actual INSERT.</summary>
    public void RequireJobOrigin(JobOrigin origin, IUnitOfWork expectedUnit)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var announcement = RequireAnnouncement(expectedUnit);
        if (!ReferenceEquals(origin.Execution, this) || !ReferenceEquals(origin.CurrentInvocation, _current)
            || !ReferenceEquals(origin.Dispatch, _dispatch) || !ReferenceEquals(origin.Transaction, unitOfWork.Transaction)
            || origin.Announcement != announcement
            || !ReferenceEquals(origin.Invocation, _dispatch?.Origin.Invocation ?? _current))
            throw new InvalidOperationException("The job origin no longer belongs to the active producer invocation.");
    }

    /// <summary>Opaque scheduling origin, with diagnostic data that grants no standalone authority.</summary>
    public sealed class JobOrigin
    {
        internal JobOrigin(ProducerExecution execution, Invocation currentInvocation, Invocation invocation,
            object? dispatch, DbTransaction transaction, Type operationType, TenantContextAnnouncement announcement,
            string traceparent, UserId? causalActor, Guid? incomingEventId, BackgroundJobId? parentJobId)
        {
            Execution = execution;
            CurrentInvocation = currentInvocation;
            Invocation = invocation;
            Dispatch = dispatch;
            Transaction = transaction;
            OperationType = operationType;
            Announcement = announcement;
            Traceparent = traceparent;
            CausalActor = causalActor;
            IncomingEventId = incomingEventId;
            ParentJobId = parentJobId;
        }
        internal ProducerExecution Execution { get; }
        internal Invocation CurrentInvocation { get; }
        internal Invocation Invocation { get; }
        internal object? Dispatch { get; }
        internal DbTransaction Transaction { get; }
        public Type OperationType { get; }
        public TenantContextAnnouncement Announcement { get; }
        public string Traceparent { get; }
        public UserId? CausalActor { get; }
        public Guid? IncomingEventId { get; }
        public BackgroundJobId? ParentJobId { get; }
    }

    /// <summary>Captures successful-save attribution without retaining ambient Activity state.</summary>
    public Origin CaptureOrigin()
    {
        var announcement = RequireAnnouncement();
        if (_dispatch is not null)
        {
            Poison();
            throw new InvalidOperationException("Domain translation cannot produce more domain events.");
        }

        if (Activity.Current is not { IdFormat: ActivityIdFormat.W3C, Id: { } traceparent })
        {
            throw new InvalidOperationException("An enrolled producer requires a W3C Activity.");
        }

        return new Origin(this, _current!, announcement, traceparent, _current!.CausalActor);
    }

    /// <summary>Validates an origin against this physical transaction and its current owner.</summary>
    internal void RequireOrigin(Origin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var live = RequireAnnouncement();
        if (!ReferenceEquals(origin.Execution, this) || live != origin.Announcement
            || !ReferenceEquals(OwnerOf(_current!), OwnerOf(origin.Invocation)))
        {
            throw new InvalidOperationException("The domain event origin no longer belongs to this transaction.");
        }
    }

    internal void RequireOwner(Invocation owner)
    {
        if (!ReferenceEquals(_current, owner) || !owner.IsTransactionOwner)
        {
            throw new InvalidOperationException("Only the active physical transaction owner can drain domain events.");
        }

        EnsureCurrentInvocation();
    }

    internal void Poison() => unitOfWork.MarkRollbackOnly();

    private static Invocation OwnerOf(Invocation invocation)
    {
        while (invocation.Parent is not null) invocation = invocation.Parent;
        return invocation;
    }

    /// <summary>
    /// Installs dispatch metadata synchronously; does not reopen the originating child
    /// or alter AsyncLocal/Activity state across an asynchronous helper boundary.
    /// </summary>
    public IDisposable EnterDomainDispatch(Origin origin, Guid domainEventId)
    {
        RequireOrigin(origin);
        RequireOwner(_current!);
        if (_dispatch is not null || domainEventId == Guid.Empty)
        {
            Poison();
            throw new InvalidOperationException("Domain dispatch must be sequential and carry an event identity.");
        }

        _dispatch = new DomainDispatch(origin, domainEventId);
        return new DispatchScope(this, _dispatch);
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
        RecordReceipt(_dispatch?.Origin.Invocation ?? _current!);
    }

    /// <summary>Records a saved fact against its retained invocation, including disposed children.</summary>
    public void RecordProducedWork(Origin origin)
    {
        RequireOrigin(origin);
        RecordReceipt(origin.Invocation);
    }

    private static void RecordReceipt(Invocation origin)
    {
        for (var invocation = origin; invocation is not null; invocation = invocation.Parent)
        {
            invocation.HasProducedWork = true;
        }
    }

    /// <summary>Opaque attribution captured at the saving boundary.</summary>
    public sealed class Origin
    {
        internal Origin(ProducerExecution execution, Invocation invocation,
            TenantContextAnnouncement announcement, string traceparent, UserId? causalActor)
        {
            Execution = execution;
            Invocation = invocation;
            Announcement = announcement;
            Traceparent = traceparent;
            CausalActor = causalActor;
        }

        internal ProducerExecution Execution { get; }
        internal Invocation Invocation { get; }
        internal TenantContextAnnouncement Announcement { get; }
        internal string Traceparent { get; }
        internal UserId? CausalActor { get; }
    }

    private sealed record DomainDispatch(Origin Origin, Guid EventId);

    // The collector owns the lexical scope token. ProducerExecution retains only
    // immutable metadata, so it does not share disposal ownership with the caller.
    private sealed class DispatchScope(ProducerExecution execution, DomainDispatch dispatch) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(execution._dispatch, dispatch)) execution._dispatch = null;
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
            TenantContextAnnouncement? announcement, bool hasOwner, bool isTransactionOwner,
            Activity? ownedActivity, UserId? causalActor, Guid? incomingEventId,
            Type? operationType, BackgroundJobId? parentJobId)
        {
            _execution = execution;
            Parent = parent;
            Announcement = announcement;
            HasOwner = hasOwner;
            IsTransactionOwner = isTransactionOwner;
            _ownedActivity = ownedActivity;
            CausalActor = causalActor;
            IncomingEventId = incomingEventId;
            OperationType = operationType;
            ParentJobId = parentJobId;
        }

        internal Invocation? Parent { get; }
        internal TenantContextAnnouncement? Announcement { get; }
        internal bool HasOwner { get; }
        internal bool IsTransactionOwner { get; }
        internal UserId? CausalActor { get; }
        internal Guid? IncomingEventId { get; }
        internal Type? OperationType { get; }
        internal BackgroundJobId? ParentJobId { get; }

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

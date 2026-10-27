using System.Data.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging;

namespace LearnStack.Application.Audit;

/// <summary>Independent per-occurrence MUST state; business capture completion never acknowledges these events.</summary>
public sealed class AuthorizationEvidenceCoordinator(
    IAuditStore store, IAuditStateCapture capture, IUnitOfWork unit, IClock clock, IGuidFactory guids,
    Func<IOperationInvocationContext> current, IEnumerable<IAuthorizationTransportSource> transportSources,
    ILogger<AuthorizationEvidenceCoordinator> logger)
    : AuthorizationAuditContext.MintingSource, IAuthorizationRefusalReporter, IDisposable
{
    private readonly Dictionary<OperationInvocation, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<OperationInvocation, AuthorizationAuditContext> _contexts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ConfirmedClaimAssertion, Entry> _assertions = new(ReferenceEqualityComparer.Instance);
    private readonly IAuthorizationTransportSource[] _transports = [.. transportSources];
    private readonly SemaphoreSlim _writes = new(1, 1);
    private int _depth;
    private int _closed;
    private bool _compositionFailed;

    public ExecutionScope BeginExecution()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (_depth == 0 && (_entries.Count != 0 || _compositionFailed))
            throw new InvalidOperationException("Authorization evidence still belongs to an earlier execution.");
        return new ExecutionScope(this, ++_depth);
    }

    public void Observe(OperationInvocation invocation, ProtectedOperationDecision? decision)
    {
        if (!HasAttribution(invocation) && !(invocation.IsActive
            && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed }
            && invocation.Operation is LearnStack.SharedKernel.Tenancy.IProvisionsTenant)) return;
        try
        {
            var context = Snapshot(invocation, decision);
            capture.AttachAuthorization(context);
            _contexts[invocation] = context;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { CompositionFailed(); }
    }

    public void ReportRecheck(ProtectedOperationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Outcome != ProtectedOperationOutcome.Allowed)
            throw new ArgumentException("A successful recheck requires an allowed current decision.", nameof(decision));
        Observe(decision.Invocation, decision);
        if (_compositionFailed) throw new AuditWriteFailedException("Trusted authorization evidence could not be composed.");
    }

    public void ReportResourceRefusal(ProtectedOperationDecision decision, AuthorizationRefusalReason reason)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        RecordDenial(decision.Invocation, decision, decision.Outcome == ProtectedOperationOutcome.NotCovered
            ? AuthorizationRefusalReason.Coverage : reason,
            decision.Outcome == ProtectedOperationOutcome.NotCovered ? "lockey_not_found" : "lockey_forbidden");
    }

    public void RecordPolicyRefusal(OperationInvocation invocation, ProtectedOperationDecision? decision, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Code is not ("forbidden" or "not_found" or "resource_scope_violation" or "feature_disabled" or "limit_exceeded")) return;
        RecordDenial(invocation, decision, error.Code == "not_found" ? AuthorizationRefusalReason.Coverage
            : error.Code == "resource_scope_violation" ? AuthorizationRefusalReason.ResourceScope : AuthorizationRefusalReason.Capability,
            error.Message.Key, _contexts.GetValueOrDefault(invocation));
    }

    /// <summary>The API has already decided its masked refusal; audit failure never replaces that refusal.</summary>
    public async Task RecordConfirmedAssertionAsync(ConfirmedClaimAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        await _writes.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_assertions.TryGetValue(assertion, out var entry))
            {
                if (!assertion.IsActive) { CompositionFailed(); return; }
                var authorization = "{\"version\":1,\"policy\":\"tenancy.assertion.reject_claim_context\",\"source\":\"http\",\"memberships\":[],\"roles\":[]"
                    + (assertion.Route is { } route ? ",\"route\":" + AuditJson.Quote(route) : string.Empty) + "}";
                var draft = new AuditEntryDraft
                {
                    Id = AuditEntryId.From(guids.NewUuidV7()),
                    TenantId = assertion.TenantId,
                    OrganizationId = assertion.OrganizationId,
                    ActorUserId = assertion.Actor,
                    ActorEmail = null,
                    ModuleName = "tenancy",
                    Operation = "tenancy.assertion.reject_claim_context",
                    OperationType = OperationType.SecurityEvent,
                    OperationClass = OperationClass.Must,
                    EntityType = null,
                    EntityId = null,
                    Outcome = AuditOutcome.Denied,
                    ErrorKey = "lockey_tenant_mismatch",
                    Reason = null,
                    BeforeState = null,
                    AfterState = null,
                    Changes = null,
                    CorrelationId = assertion.CorrelationId,
                    IpAddress = assertion.Address,
                    UserAgent = assertion.UserAgent,
                    Timestamp = clock.UtcNow,
                    Metadata = "{\"authorization\":" + authorization
                        + ",\"assertion_source\":\"membership_confirmed_header\",\"mismatch_reason\":\"conflicts_with_confirmed_context\",\"dimension\":"
                        + AuditJson.Quote(assertion.Dimension == ClaimAssertionDimension.Tenant ? "tenant" : "organization") + "}",
                };
                entry = new Entry(ForOccurrence(AuthorizationEvidenceKind.ClaimAssertion, draft));
                _assertions.Add(assertion, entry);
            }
            if (entry.Acknowledged || entry.StandaloneFailed) return;
            try
            {
                await store.WriteAuthorizationStandaloneAsync(entry.Occurrence, CancellationToken.None).ConfigureAwait(false);
                entry.Acknowledged = true;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
            { entry.StandaloneFailed = true; /* The MUST store reports the explicit loss. */ }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { CompositionFailed(); }
        finally { _writes.Release(); }
    }

    private void RecordDenial(OperationInvocation invocation, ProtectedOperationDecision? decision, AuthorizationRefusalReason reason,
        string errorKey = "lockey_forbidden", AuthorizationAuditContext? observedContext = null)
    {
        if (_entries.ContainsKey(invocation) || !HasAttribution(invocation)) return;
        try
        {
            var context = observedContext ?? Snapshot(invocation, decision);
            capture.AttachAuthorization(context);
            var metadata = "{\"authorization\":" + context.ToJson()
                + ",\"denial_reason\":" + AuditJson.Quote(reason switch
                {
                    AuthorizationRefusalReason.Capability => "capability",
                    AuthorizationRefusalReason.ResourceScope => "resource_scope",
                    AuthorizationRefusalReason.Delegation => "delegation",
                    AuthorizationRefusalReason.Ownership => "ownership",
                    AuthorizationRefusalReason.Coverage => "coverage",
                    _ => throw new InvalidOperationException("Authorization refusal is unknown."),
                }) + "}";
            var draft = new AuditEntryDraft
            {
                Id = AuditEntryId.From(guids.NewUuidV7()),
                TenantId = context.TenantId,
                OrganizationId = context.OrganizationId,
                ActorUserId = context.Actor,
                ActorEmail = null,
                ModuleName = "identity",
                Operation = "identity.authorization.deny",
                OperationType = OperationType.SecurityEvent,
                OperationClass = OperationClass.Must,
                EntityType = null,
                EntityId = null,
                Outcome = AuditOutcome.Denied,
                ErrorKey = errorKey,
                Reason = null,
                BeforeState = null,
                AfterState = null,
                Changes = null,
                CorrelationId = invocation.Context.CorrelationId,
                IpAddress = context.Address,
                UserAgent = context.UserAgent,
                Timestamp = clock.UtcNow,
                Metadata = metadata,
            };
            _entries.Add(invocation, new Entry(ForOccurrence(AuthorizationEvidenceKind.Denial, draft)));
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { CompositionFailed(); }
    }

    private AuthorizationAuditContext Snapshot(OperationInvocation invocation, ProtectedOperationDecision? decision)
    {
        if (_depth == 0 || !ReferenceEquals(current().CurrentInvocation, invocation))
            throw new InvalidOperationException("Authorization evidence requires the actual current invocation.");
        var transports = _transports.Select(source => source.Capture(invocation)).Where(value => value is not null).Take(2).ToArray();
        if (transports.Length > 1) throw new InvalidOperationException("Authorization transport provenance is ambiguous.");
        return ForAuditContext(invocation, decision, transports.SingleOrDefault());
    }

    private static bool HasAttribution(OperationInvocation invocation) => invocation.IsActive
        && invocation.TenantId is { } tenant && tenant != LearnStack.SharedKernel.Identifiers.TenantId.PlatformSentinel
        && (invocation.Candidate is { IsActive: true } || invocation.TrustedSource is { IsActive: true });

    /// <summary>Called only by the actual owner after draining domain work and before final commit admission.</summary>
    public async Task<CommitBatch?> BeforeOwnerCompletionAsync(IUnitOfWork owner, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(owner, unit) || _depth == 0 || !owner.HasActiveTransaction || owner.IsRollbackOnly)
            throw new InvalidOperationException("Authorization evidence requires its actual completion owner.");
        if (_compositionFailed) throw new AuditWriteFailedException("Trusted authorization evidence could not be composed.");
        if (owner.Mode == TransactionMode.ReadOnly)
        {
            await AcknowledgeStandaloneAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var transaction = owner.Transaction ?? throw new InvalidOperationException("Authorization evidence lost its owner transaction.");
        var announcement = owner.GetTenantContextAnnouncement(transaction)
            ?? throw new InvalidOperationException("Authorization evidence owner has no current announcement.");
        var entries = _entries.Values.Where(entry => !entry.Acknowledged).ToArray();
        var batch = new CommitBatch(this, owner, transaction, announcement.Generation, entries);
        foreach (var entry in entries)
        {
            if (entry.Pending is not null) throw new InvalidOperationException("A joiner cannot rewrite pending authorization evidence.");
            await store.WriteAuthorizationPendingAsync(owner, entry.Occurrence, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(owner.Transaction, batch.Transaction)
                || owner.GetTenantContextAnnouncement(batch.Transaction)?.Generation != batch.Generation)
            {
                owner.MarkRollbackOnly();
                throw new AuditWriteFailedException("Authorization evidence lost its original physical owner generation.");
            }
            entry.Pending = batch;
        }
        return batch;
    }

    public void RecordOwnerOutcome(CommitBatch? batch, bool committed)
    {
        if (batch is null) return;
        if (!ReferenceEquals(batch.Coordinator, this) || !ReferenceEquals(batch.Unit, unit))
            throw new InvalidOperationException("Authorization completion does not belong to this owner generation.");
        if (batch.Finished) return;
        if (committed && unit.HasActiveTransaction)
            throw new InvalidOperationException("A live transaction cannot acknowledge authorization evidence.");
        batch.Finished = true;
        foreach (var entry in batch.Entries)
        {
            if (!ReferenceEquals(entry.Pending, batch)) continue;
            entry.Pending = null;
            if (committed) entry.Acknowledged = true;
        }
    }

    /// <summary>An outer execution without a physical owner cannot publish an absorbed refusal before acknowledgement.</summary>
    public async Task BeforeSuccessAsync(CancellationToken cancellationToken)
    {
        if (_compositionFailed) throw new AuditWriteFailedException("Trusted authorization evidence could not be composed.");
        if (!unit.HasActiveTransaction) await AcknowledgeStandaloneAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AcknowledgeStandaloneAsync(CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in _entries.Values.Where(entry => !entry.Acknowledged))
            {
                if (entry.Pending is not null) throw new InvalidOperationException("A live business transaction still owns authorization evidence.");
                if (entry.StandaloneFailed) throw new AuditWriteFailedException("Authorization evidence was not acknowledged.");
                try
                {
                    await store.WriteAuthorizationStandaloneAsync(entry.Occurrence, cancellationToken).ConfigureAwait(false);
                    entry.Acknowledged = true;
                }
                catch { entry.StandaloneFailed = true; throw; }
            }
        }
        finally { _writes.Release(); }
    }

    private async ValueTask EndExecutionAsync(int depth)
    {
        if (_depth != depth) throw new InvalidOperationException("Authorization evidence execution scopes must close in order.");
        if (--_depth != 0) return;
        try
        {
            await ReconcileBeforeCaptureClearAsync().ConfigureAwait(false);
        }
        finally { _entries.Clear(); _contexts.Clear(); _compositionFailed = false; }
    }

    /// <summary>Reconciles a completed physical owner before ordinary capture cleanup; a prepared live owner retains its events.</summary>
    public async Task ReconcileBeforeCaptureClearAsync()
    {
        if (unit.HasActiveTransaction) return;
        // Already-refused or failed executions retain their primary result.
        // A failed standalone attempt is not retried by this same finally.
        await _writes.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var entry in _entries.Values.Where(entry => !entry.Acknowledged && !entry.StandaloneFailed))
            {
                entry.Pending = null; // A closed failed/uncertain owner never acknowledges an event.
                try
                {
                    await store.WriteAuthorizationStandaloneAsync(entry.Occurrence, CancellationToken.None).ConfigureAwait(false);
                    entry.Acknowledged = true;
                }
                catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
                { entry.StandaloneFailed = true; /* The MUST store reports this explicit refusal-audit loss. */ }
            }
        }
        finally { _writes.Release(); }
    }

    private void CompositionFailed()
    {
        if (!_compositionFailed) LogCompositionFailure(logger, null);
        _compositionFailed = true;
    }

    public void Dispose()
    {
        if (_depth != 0) throw new InvalidOperationException("Authorization evidence cleanup still has an execution owner.");
        if (Interlocked.Exchange(ref _closed, 1) == 0) { _writes.Dispose(); _assertions.Clear(); }
        GC.SuppressFinalize(this);
    }

    internal sealed class Entry(AuthorizationEvidenceOccurrence occurrence)
    {
        internal AuthorizationEvidenceOccurrence Occurrence { get; } = occurrence;
        internal bool Acknowledged { get; set; }
        internal bool StandaloneFailed { get; set; }
        internal CommitBatch? Pending { get; set; }
    }

#pragma warning disable CA1034 // Private tokens bind completion and cleanup to the actual common owner.
    public sealed class CommitBatch
    {
        internal CommitBatch(AuthorizationEvidenceCoordinator coordinator, IUnitOfWork unit, DbTransaction transaction,
            long generation, Entry[] entries)
        { Coordinator = coordinator; Unit = unit; Transaction = transaction; Generation = generation; Entries = entries; }
        internal AuthorizationEvidenceCoordinator Coordinator { get; }
        internal IUnitOfWork Unit { get; }
        internal DbTransaction Transaction { get; }
        internal long Generation { get; }
        internal Entry[] Entries { get; }
        internal bool Finished { get; set; }
    }

    public sealed class ExecutionScope : IAsyncDisposable
    {
        private readonly AuthorizationEvidenceCoordinator _owner;
        private readonly int _depth;
        private int _closed;
        internal ExecutionScope(AuthorizationEvidenceCoordinator owner, int depth) { _owner = owner; _depth = depth; }
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _closed, 1) == 0 ? _owner.EndExecutionAsync(_depth) : ValueTask.CompletedTask;
    }
#pragma warning restore CA1034

    private static readonly Action<ILogger, Exception?> LogCompositionFailure = LoggerMessage.Define(
        LogLevel.Critical, new EventId(77, "AuthorizationEvidenceCompositionFailed"),
        "Trusted authorization evidence could not be composed. An otherwise-successful execution cannot complete.");
}

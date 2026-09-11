namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// Turns one intent plus its request's captured changes into the row to insert.
/// </summary>
/// <remarks>
/// <para>
/// <b>One composer, because there are two writers.</b> The in-transaction path composes a
/// row before <c>COMMIT</c> and the reconcile composes one after the transaction resolved,
/// and they had a private composer each — which diverged immediately: only the happy-path
/// row carried a snapshot, so every <c>denied</c>, <c>failed</c> and <c>indeterminate</c>
/// row said nothing about what was attempted. That is the class of row
/// <see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>
/// calls the common case it protects, and the class an investigator reads first.
/// </para>
/// <para>
/// <b>Two entry points and no optional arguments.</b> The first shared composer still took
/// the actor and the correlation id as optional parameters, the reconcile passed them and
/// the in-transaction write did not, so every successful MUST row carried neither — and it
/// took the outcome from its caller, so the in-transaction write hard-coded
/// <c>success</c> for every intent in the scope. Each entry point now reads everything
/// from the intent and decides the outcome itself, by the one rule for its moment
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment 6
/// §§ 2, 3</see>). A caller has nothing left to forget.
/// </para>
/// <para>
/// It lives in SharedKernel because its two callers sit on opposite sides of a boundary:
/// the pipeline behaviour is in <c>LearnStack.Application</c> and the store is in
/// <c>LearnStack.Infrastructure.Audit</c>, and neither may reference the other.
/// </para>
/// </remarks>
public static class AuditDraftComposer
{
    /// <summary>
    /// The row the owning frame writes on the business transaction, immediately before
    /// <c>COMMIT</c>.
    /// </summary>
    /// <remarks>
    /// <b>An intent with no recorded result is the owning request's own, and it
    /// succeeded.</b> Every nested request has closed its frame by the time the owner's
    /// handler returns, so the only frame still open is the owner's — and
    /// <c>TransactionBehavior</c> reaches this write only on that handler's success. A
    /// nested request that was refused keeps its refusal; one that threw keeps
    /// <c>failed</c>, and its exception has already marked the unit rollback-only, so the
    /// row never commits as written.
    /// </remarks>
    /// <param name="intent">The parked declaration.</param>
    /// <param name="changes">
    /// The changes its request captured — <see cref="IAuditStateCapture.ChangesOf"/>.
    /// </param>
    public static AuditEntryDraft InTransaction(
        AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var result = intent.Result ?? AuditIntentResult.Succeeded;

        return Compose(intent, changes, result.Outcome, result.ErrorKey, intent.DeclaredAt);
    }

    /// <summary>
    /// The row the reconcile writes after the transaction resolved, for an intent the
    /// commit did not carry.
    /// </summary>
    /// <remarks>
    /// <para>The outcome, in order:</para>
    /// <list type="number">
    /// <item>
    /// <b>A refusal the request returned</b> — <c>denied</c> or <c>failed</c>, with its key.
    /// It is a fact about the operation that no <c>COMMIT</c> changes.
    /// </item>
    /// <item>
    /// <b><c>indeterminate</c></b> when the <c>COMMIT</c>'s fate is unknown — the row's
    /// honest content, and a question a reader filtering for it is asking about the
    /// database rather than the caller.
    /// </item>
    /// <item>
    /// <b><c>success</c></b> for a request that succeeded in a transaction that committed.
    /// Only a SHOULD or MAY row reaches that: a committed MUST row is durable already.
    /// </item>
    /// <item>
    /// <b><c>failed</c></b> otherwise — the request threw, or it succeeded and its work did
    /// not persist. The refusal that caused that is on its own request's row, joined by
    /// the correlation id.
    /// </item>
    /// </list>
    /// </remarks>
    /// <param name="intent">The parked declaration, with its request's result.</param>
    /// <param name="changes">
    /// The changes its request captured — <see cref="IAuditStateCapture.ChangesOf"/>.
    /// </param>
    /// <param name="state">What the owning frame recorded about the transaction.</param>
    /// <param name="timestamp">
    /// A <b>fresh</b> reading, never the intent's <c>DeclaredAt</c>: the in-transaction row
    /// that may already exist under this id carries that, and the composite primary key is
    /// what keeps the commit-in-doubt pair legal instead of raising <c>23505</c>.
    /// </param>
    public static AuditEntryDraft Reconciled(
        AuditIntent intent,
        IReadOnlyList<CapturedEntityChange> changes,
        AuditIntentState state,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(intent);

        (AuditOutcome outcome, string? errorKey) = intent.Result switch
        {
            { Threw: false, Outcome: not AuditOutcome.Success } refused =>
                (refused.Outcome, refused.ErrorKey),
            _ when state == AuditIntentState.Indeterminate => (AuditOutcome.Indeterminate, null),
            { Threw: false } when state == AuditIntentState.Committed => (AuditOutcome.Success, null),
            _ => (AuditOutcome.Failed, null),
        };

        return Compose(intent, changes, outcome, errorKey, timestamp);
    }

    private static AuditEntryDraft Compose(
        AuditIntent intent,
        IReadOnlyList<CapturedEntityChange> changes,
        AuditOutcome outcome,
        string? errorKey,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var subject = Subject.Of(intent, changes);

        return new AuditEntryDraft
        {
            Id = intent.Id,
            TenantId = intent.TenantId,
            OrganizationId = intent.OrganizationId,
            ActorUserId = intent.ActorUserId,
            ActorEmail = null,
            ModuleName = intent.ModuleName,
            Operation = intent.Operation,
            OperationType = intent.OperationType,
            OperationClass = intent.OperationClass,
            EntityType = intent.EntityType?.Name,
            EntityId = subject.EntityId,
            Outcome = outcome,
            ErrorKey = errorKey,
            Reason = null,

            // The subject's EARLIEST capture's before and its LATEST capture's after, nulls
            // included. Skipping nulls looks like tidying and is not: the interceptor sets
            // BeforeJson to null to say the entity did not exist and AfterJson to null to
            // say it no longer does, so those two are the only captures that carry that
            // meaning (ADR-0044 Amendment 5 § 2).
            BeforeState = subject.Before,
            AfterState = subject.After,
            Changes = Diff(subject.OfType),

            CorrelationId = intent.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = timestamp,
            Metadata = null,
        };
    }

    /// <summary>
    /// The <c>changes</c> column: a JSON <b>array</b> of <c>{ path, before, after }</c>.
    /// </summary>
    /// <remarks>
    /// Composed as text rather than serialised from objects, because each slot already
    /// holds JSON text — re-serialising would quote a document into one long escaped
    /// string. ADR-0016's polymorphic object-or-array shape is withdrawn: two readers parse
    /// this column, and a shape that changes with the row's arity is a shape each of them
    /// gets wrong once (ADR-0044 § 7).
    /// </remarks>
    private static string? Diff(List<CapturedEntityChange> captures)
    {
        var fields = captures.SelectMany(change => change.Fields).ToList();

        if (fields.Count == 0)
        {
            return null;
        }

        var entries = fields.Select(field =>
            "{\"path\":" + AuditJson.Quote(field.Path)
            + ",\"before\":" + (field.BeforeJson ?? AuditJson.Null)
            + ",\"after\":" + (field.AfterJson ?? AuditJson.Null) + "}");

        return AuditJson.CapArray("[" + string.Join(',', entries) + "]");
    }

    /// <summary>What one intent's row is about.</summary>
    /// <param name="EntityId">The subject instance, or <c>null</c> when there is none.</param>
    /// <param name="Before">The subject's earliest captured prior state.</param>
    /// <param name="After">The subject's latest captured state.</param>
    /// <param name="OfType">
    /// Every capture of the declared type, subject or not, in capture order — which is
    /// what <c>changes</c> is built from, so a retired incumbent stays on the record.
    /// </param>
    private sealed record Subject(
        string? EntityId, string? Before, string? After, List<CapturedEntityChange> OfType)
    {
        private static readonly Subject None = new(null, null, null, []);

        /// <remarks>
        /// Matched on the declared aggregate's type name. One request captures every entity
        /// it touched and an intent is about one of them — without the filter,
        /// <c>ProvisionTenantCommand</c>'s tenant row would carry the organization's
        /// snapshot and the trail would attribute one aggregate's change to another.
        /// </remarks>
        public static Subject Of(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes)
        {
            if (intent.EntityType is null)
            {
                return None;
            }

            var ofType = changes
                .Where(change => string.Equals(
                    change.EntityType, intent.EntityType.Name, StringComparison.Ordinal))
                .ToList();

            // A designation is taken as given even when nothing of that instance was
            // captured: a request refused after the handler named its subject and before it
            // wrote is still a row about that instance, with no state to show for it.
            var entityId = intent.SubjectId ?? SoleInstance(intent, ofType);

            var own = ofType
                .Where(change => string.Equals(change.EntityId, entityId, StringComparison.Ordinal))
                .ToList();

            return own.Count == 0
                ? new Subject(entityId, null, null, ofType)
                : new Subject(entityId, own[0].BeforeJson, own[^1].AfterJson, ofType);
        }

        /// <remarks>
        /// <para>
        /// Two DIFFERENT instances of one type under one undesignated intent are refused
        /// rather than merged: nothing in the captures says which one the operation is
        /// about, so <c>entity_id</c> would name one while <c>after_state</c> described the
        /// other. That row is self-contradictory and permanent. The handler that wrote both
        /// knows, and <see cref="IAuditSubject"/> is how it says (ADR-0044 Amendment 6 § 1).
        /// </para>
        /// <para>
        /// A capture with no key cannot be told apart from another, so each counts as an
        /// instance of its own — <c>Distinct</c> over the ids used to collapse two keyless
        /// captures into one and merge them, the very thing this refuses.
        /// </para>
        /// <para>
        /// <b>An <see cref="InvalidOperationException"/>, not
        /// <see cref="AuditWriteFailedException"/>.</b> A handler that forgot to designate is
        /// a programmer error — a 500, like an unclassified operation — and the store is not
        /// down: <c>audit_unavailable</c> is a 503 that tells the caller to retry a request
        /// that will fail the same way every time (the fifth review of Packet 9). The business
        /// transaction still rolls back, because <c>TransactionBehavior</c> fails closed on any
        /// exception before its commit.
        /// </para>
        /// </remarks>
        private static string? SoleInstance(AuditIntent intent, List<CapturedEntityChange> ofType)
        {
            var identities = ofType
                .Where(change => change.EntityId is not null)
                .Select(change => change.EntityId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var instances = identities.Count + ofType.Count(change => change.EntityId is null);

            return instances switch
            {
                0 => null,
                1 => identities.SingleOrDefault(),
                _ => throw new InvalidOperationException(
                    $"The intent for {intent.Operation} names {intent.EntityType!.Name} and the "
                    + $"request captured {instances} different instances of it without "
                    + "designating one. An audit row is about one aggregate: composing these "
                    + "would put one instance's id beside another's state, on a table nothing "
                    + "can correct. The handler that writes both designates its subject through "
                    + "IAuditSubject (ADR-0044 Amendment 6 § 1)."),
            };
        }
    }
}

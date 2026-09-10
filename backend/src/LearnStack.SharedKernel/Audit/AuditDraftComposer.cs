namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// Turns one intent plus the request's captured changes into the row to insert.
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
/// It lives in SharedKernel because its two callers sit on opposite sides of a boundary:
/// the pipeline behaviour is in <c>LearnStack.Application</c> and the store is in
/// <c>LearnStack.Infrastructure.Audit</c>, and neither may reference the other.
/// </para>
/// </remarks>
public static class AuditDraftComposer
{
    /// <summary>Composes the row for one intent.</summary>
    /// <param name="intent">The parked declaration, which carries the tenant.</param>
    /// <param name="changes">Every change the request captured, in capture order.</param>
    /// <param name="outcome">What became of the operation.</param>
    /// <param name="timestamp">
    /// The row's instant. The in-transaction row takes the intent's <c>DeclaredAt</c>; a
    /// standalone re-write takes a <b>fresh</b> reading, which is what keeps the
    /// commit-in-doubt pair legal under the composite primary key instead of raising
    /// <c>23505</c>.
    /// </param>
    /// <param name="actorUserId">Who acted, when there was a principal.</param>
    /// <param name="correlationId">Matches the trace id in logs and the Problem Details body.</param>
    /// <param name="errorKey">The refusal's localization key, when it was refused.</param>
    public static AuditEntryDraft Compose(
        AuditIntent intent,
        IReadOnlyList<CapturedEntityChange> changes,
        AuditOutcome outcome,
        DateTimeOffset timestamp,
        Identifiers.UserId? actorUserId = null,
        string? correlationId = null,
        string? errorKey = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(changes);

        var matching = Matching(intent, changes);

        return new AuditEntryDraft
        {
            Id = intent.Id,
            TenantId = intent.TenantId,
            OrganizationId = intent.OrganizationId,
            ActorUserId = actorUserId,
            ActorEmail = null,
            ModuleName = intent.ModuleName,
            Operation = intent.Operation,
            OperationType = intent.OperationType,
            OperationClass = intent.OperationClass,
            EntityType = intent.EntityType?.Name,
            EntityId = matching.Count == 0 ? null : matching[0].EntityId,
            Outcome = outcome,
            ErrorKey = errorKey,
            Reason = null,

            // The EARLIEST capture's before and the LATEST capture's after, nulls
            // included. Skipping nulls looks like tidying and is not: the interceptor sets
            // BeforeJson to null to say the entity did not exist and AfterJson to null to
            // say it no longer does, so those two are the only captures that carry that
            // meaning (ADR-0044 Amendment 5 § 2).
            BeforeState = matching.Count == 0 ? null : matching[0].BeforeJson,
            AfterState = matching.Count == 0 ? null : matching[^1].AfterJson,
            Changes = Diff(matching),

            CorrelationId = correlationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = timestamp,
            Metadata = null,
        };
    }

    /// <summary>
    /// The captures this intent's row is about.
    /// </summary>
    /// <remarks>
    /// Matched on the declared aggregate's type name. One request captures every entity it
    /// touched and an intent is about one of them — without the filter,
    /// <c>ProvisionTenantCommand</c>'s tenant row would carry the organization's snapshot
    /// and the trail would attribute one aggregate's change to another.
    /// <para>
    /// Two DIFFERENT instances of one type under one intent are refused rather than
    /// merged: the type-name filter cannot tell them apart, so <c>entity_id</c> would name
    /// one while <c>after_state</c> described the other, and the pointers in
    /// <c>changes</c> carry no instance. That row is self-contradictory and permanent.
    /// </para>
    /// </remarks>
    private static List<CapturedEntityChange> Matching(
        AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes)
    {
        if (intent.EntityType is null)
        {
            return [];
        }

        var matching = changes
            .Where(change => string.Equals(
                change.EntityType, intent.EntityType.Name, StringComparison.Ordinal))
            .ToList();

        var identities = matching
            .Select(change => change.EntityId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return identities.Count > 1
            ? throw new AuditWriteFailedException(
                $"The intent for {intent.Operation} names {intent.EntityType.Name} and the "
                + $"request captured {identities.Count} different instances of it. An audit "
                + "row is about one aggregate: composing these would put one instance's id "
                + "beside another's state, on a table nothing can correct. Declare one "
                + "intent per audited resource (ADR-0044 § 3).")
            : matching;
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
    private static string? Diff(List<CapturedEntityChange> matching)
    {
        var fields = matching.SelectMany(change => change.Fields).ToList();

        if (fields.Count == 0)
        {
            return null;
        }

        var entries = fields.Select(field =>
            "{\"path\":" + System.Text.Json.JsonSerializer.Serialize(field.Path)
            + ",\"before\":" + (field.BeforeJson ?? AuditJson.Null)
            + ",\"after\":" + (field.AfterJson ?? AuditJson.Null) + "}");

        return AuditJson.CapArray("[" + string.Join(',', entries) + "]");
    }
}

using System.Net;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// A complete <c>audit_log</c> row, composed once and inserted once.
/// </summary>
/// <remarks>
/// <para>
/// <b>A SharedKernel record, not the module's aggregate, and that is deliberate.</b>
/// <c>AuditEntry</c> stays the Audit module's aggregate and is the <em>read</em> model
/// for the audit admin API; the write path carries this draft and
/// <c>PostgresAuditStore</c> turns it into one parameterised <c>INSERT</c>. Mapping the
/// aggregate into every module's <c>DbContext</c> would require SharedKernel to
/// reference <c>LearnStack.Modules.Audit.Domain</c> — a circular project reference —
/// and would make every module's Infrastructure assembly name <c>AuditEntry</c>, which
/// is what <c>Modules_Do_Not_Write_AuditLog_Directly</c> exists to prevent
/// (<see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// § Implementation Notes</see>).
/// </para>
/// <para>
/// <b>Written once and never updated.</b> By the time the row is composed every field
/// is known, so there is no second phase and no enrichment <c>UPDATE</c>;
/// <see cref="IAuditStore"/> has no update method.
/// </para>
/// <para>
/// <b>Every member is <c>required</c> and named at the call site, and that is a
/// correctness property rather than a style.</b> The row has twenty-two fields and
/// <b>eleven</b> of them are <c>string?</c>, six of those consecutive —
/// <c>ErrorKey</c>, <c>Reason</c>, <c>BeforeState</c>, <c>AfterState</c>,
/// <c>Changes</c>, <c>CorrelationId</c>. A positional constructor accepts any
/// permutation of those without a diagnostic, and the mistake lands in the one table in
/// the schema whose rows cannot be corrected: <c>learnstack_app</c> holds no
/// <c>UPDATE</c>, <c>learnstack_platform</c>'s names six columns of which
/// <c>operation</c> and <c>entity_type</c> are not two, and the owner is stopped by
/// <c>audit_log_append_only_guard</c>. So a transposed <c>Reason</c> and
/// <c>ErrorKey</c> is permanent. <c>required</c> init-only properties make the compiler
/// refuse a construction that omits a field and make every value arrive under its own
/// name; <c>with</c> still works for the one caller that needs it, the standalone
/// re-write of an <c>Indeterminate</c> row, which changes only the timestamp and the
/// outcome.
/// </para>
/// </remarks>
public sealed record AuditEntryDraft
{
    /// <summary>Minted app-side at step 3. A commit-in-doubt pair shares it.</summary>
    public required AuditEntryId Id { get; init; }

    /// <summary>The tenant the transaction announced.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The organization, or <c>null</c> for a tenant-wide row.</summary>
    public required OrganizationId? OrganizationId { get; init; }

    /// <summary>
    /// Who acted. Deliberately <b>never redacted</b>: once the <c>users</c> row is erased
    /// it is an orphan surrogate with no path back to a natural person, which is what
    /// keeps the row's existence auditable after erasure and the probe-detection queries
    /// answerable.
    /// </summary>
    public required UserId? ActorUserId { get; init; }

    /// <summary>Redactable under the GDPR path.</summary>
    public required string? ActorEmail { get; init; }

    /// <summary>The owning module's short name.</summary>
    public required string ModuleName { get; init; }

    /// <summary>The dotted slug.</summary>
    public required string Operation { get; init; }

    /// <summary>What kind of act this was.</summary>
    public required OperationType OperationType { get; init; }

    /// <summary>The declared tier.</summary>
    public required OperationClass OperationClass { get; init; }

    /// <summary>The aggregate the act was about, when there is one.</summary>
    public required string? EntityType { get; init; }

    /// <summary>Its id, rendered as text.</summary>
    public required string? EntityId { get; init; }

    /// <summary>Success, denied, failed, or indeterminate.</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>The refusal's localization key, when it was refused.</summary>
    public required string? ErrorKey { get; init; }

    /// <summary>
    /// Why a cross-tenant access happened — <c>EnterPlatformAdminScope(reason)</c>'s
    /// operator-authored slug — or the denial's cause. Never caller-supplied text.
    /// </summary>
    public required string? Reason { get; init; }

    /// <summary>The prior snapshot, redacted and size-capped.</summary>
    public required string? BeforeState { get; init; }

    /// <summary>The new snapshot, redacted and size-capped.</summary>
    public required string? AfterState { get; init; }

    /// <summary>The per-field diff, always a JSON array.</summary>
    public required string? Changes { get; init; }

    /// <summary>Matches the trace id in logs and the Problem Details body.</summary>
    public required string? CorrelationId { get; init; }

    /// <summary>
    /// Redactable under the GDPR path. <c>System.Net.IPAddress</c>, not <c>string</c>:
    /// the column is <c>inet</c>, Npgsql maps this type onto it with no configuration,
    /// and a <c>string</c> maps onto <c>text</c> — which PostgreSQL will not assign to
    /// <c>inet</c>. The same call the aggregate that reads this column already made.
    /// </summary>
    public required IPAddress? IpAddress { get; init; }

    /// <summary>Redactable under the GDPR path.</summary>
    public required string? UserAgent { get; init; }

    /// <summary>
    /// Always supplied by the store from <c>IClock</c> — the intent's <c>DeclaredAt</c>
    /// for the in-transaction row, a fresh reading for a standalone re-write. The
    /// column's <c>DEFAULT now()</c> is a backstop for a row inserted by something other
    /// than the store, and never the ordinary source.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Structured context the operation supplies — the asserted tenant on a rejected
    /// assertion, the subject on a redaction. Not a place for the payload.
    /// </summary>
    public required string? Metadata { get; init; }
}

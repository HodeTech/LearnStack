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
/// </remarks>
/// <param name="Id">Minted app-side at step 3. A commit-in-doubt pair shares it.</param>
/// <param name="TenantId">The tenant the transaction announced.</param>
/// <param name="OrganizationId">The organization, or <c>null</c> for a tenant-wide row.</param>
/// <param name="ActorUserId">
/// Who acted. Deliberately <b>never redacted</b>: once the <c>users</c> row is erased it
/// is an orphan surrogate with no path back to a natural person, which is what keeps the
/// row's existence auditable after erasure and the probe-detection queries answerable.
/// </param>
/// <param name="ActorEmail">Redactable under the GDPR path.</param>
/// <param name="ModuleName">The owning module's short name.</param>
/// <param name="Operation">The dotted slug.</param>
/// <param name="OperationType">What kind of act this was.</param>
/// <param name="OperationClass">The declared tier.</param>
/// <param name="EntityType">The aggregate the act was about, when there is one.</param>
/// <param name="EntityId">Its id, rendered as text.</param>
/// <param name="Outcome">Success, denied, failed, or indeterminate.</param>
/// <param name="ErrorKey">The refusal's localization key, when it was refused.</param>
/// <param name="Reason">
/// Why a cross-tenant access happened — <c>EnterPlatformAdminScope(reason)</c>'s
/// operator-authored slug — or the denial's cause. Never caller-supplied text.
/// </param>
/// <param name="BeforeState">The prior snapshot, redacted and size-capped.</param>
/// <param name="AfterState">The new snapshot, redacted and size-capped.</param>
/// <param name="Changes">The per-field diff, always a JSON array.</param>
/// <param name="CorrelationId">Matches the trace id in logs and the Problem Details body.</param>
/// <param name="IpAddress">Redactable under the GDPR path.</param>
/// <param name="UserAgent">Redactable under the GDPR path.</param>
/// <param name="Timestamp">
/// Always supplied by the store from <c>IClock</c> — the intent's <c>DeclaredAt</c> for
/// the in-transaction row, a fresh reading for a standalone re-write. The column's
/// <c>DEFAULT now()</c> is a backstop for a row inserted by something other than the
/// store, and never the ordinary source.
/// </param>
/// <param name="Metadata">
/// Structured context the operation supplies — the asserted tenant on a rejected
/// assertion, the subject on a redaction. Not a place for the payload.
/// </param>
public sealed record AuditEntryDraft(
    AuditEntryId Id,
    TenantId TenantId,
    OrganizationId? OrganizationId,
    UserId? ActorUserId,
    string? ActorEmail,
    string ModuleName,
    string Operation,
    OperationType OperationType,
    OperationClass OperationClass,
    string? EntityType,
    string? EntityId,
    AuditOutcome Outcome,
    string? ErrorKey,
    string? Reason,
    string? BeforeState,
    string? AfterState,
    string? Changes,
    string? CorrelationId,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset Timestamp,
    string? Metadata);

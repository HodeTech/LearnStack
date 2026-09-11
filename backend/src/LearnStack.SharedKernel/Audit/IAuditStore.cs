using System.Data.Common;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// The only sanctioned way to put a row in <c>audit_log</c>. Four writes, and
/// <b>no update</b>.
/// </summary>
/// <remarks>
/// <para>
/// Modules never write <c>audit_log</c> directly; the pipeline does it for them from
/// the catalogue's classification
/// (<see href="../../../../docs/standards/18-audit-coverage.md">Audit Coverage
/// Standards</see>). The implementation is <c>PostgresAuditStore</c> in
/// <c>LearnStack.Infrastructure.Audit</c>, and it composes each row as one
/// parameterised <c>INSERT</c> rather than through an EF entity — which is also why it
/// can never re-enter the change-tracker interceptor that feeds it.
/// </para>
/// <para>
/// <b>Why four and not three.</b>
/// <see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>
/// declared exactly three, and <see cref="WritePlatformScopeAsync"/> is the fourth
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 10</see>).
/// None of the first three could serve it: the row runs on a connection the request
/// path does not own, as a role the other three never use, and <em>before</em> the
/// operation it describes. "No update method" is untouched — this is a fourth write.
/// </para>
/// </remarks>
public interface IAuditStore
{
    /// <summary>
    /// Writes every pending MUST-class intent on the <b>ambient</b> transaction,
    /// immediately before <c>COMMIT</c>. A no-op when nothing is pending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by <c>TransactionBehavior</c>, and <b>only on the owning frame</b>
    /// (<c>IUnitOfWorkScope.IsOwner</c>): a joiner's <c>CompleteAsync</c> commits
    /// nothing, so a joiner writing here would insert a row and then report a durability
    /// it does not have. The owner flushes every intent in the scope, not only its own.
    /// </para>
    /// <para>
    /// <b>Throws on failure</b>, which is what fail-closed means here: the exception
    /// reaches <c>TransactionBehavior</c>'s catch, the business write rolls back, and
    /// the caller is answered <c>503 audit_unavailable</c> rather than committing
    /// unaudited.
    /// </para>
    /// </remarks>
    Task WritePendingAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one MUST-class row in its own short transaction, on a connection outside
    /// any business transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BEGIN; SET LOCAL app.tenant_id; SET LOCAL app.organization_id; INSERT; COMMIT</c>.
    /// <b>Both</b> session variables, from the draft: <c>audit_log</c> is org-scoped, so
    /// a row whose <c>organization_id</c> is non-null while the organization GUC is
    /// unset fails <c>WITH CHECK</c> — measured, and every <c>denied</c> row for an
    /// org-scoped resource travels this path
    /// (<see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
    /// Amendment 2 § 5</see>).
    /// </para>
    /// <para>
    /// Reached by three shapes and only these: a short-circuit at pipeline step 4 or 5; a
    /// non-MediatR caller; and the reconcile step after a <c>RolledBack</c> or
    /// <c>Indeterminate</c> outcome. A refusal at step 1 is not among them: validation runs
    /// outside the audit step, so nothing has been classified and no row is written. A granted <c>read-sensitive</c> query is <b>not</b>
    /// among them — it rides the in-transaction path, because
    /// <c>TransactionBehavior</c> has no request-kind gate.
    /// </para>
    /// <para>
    /// <b>A failure of the write is reported here, not by the caller.</b> Every exception the
    /// write throws has already been logged at <c>Critical</c>, counted on
    /// <c>learnstack_audit_standalone_write_failures_total</c> and marked on the <c>audit</c>
    /// health check — a database failure arriving as <see cref="AuditWriteFailedException"/>,
    /// anything else unchanged. A caller decides what the failure does to its response and
    /// does not raise the same alert a second time. The one exception is an
    /// <see cref="ArgumentException"/>: a draft refused before any write — one carrying
    /// <c>TenantId.PlatformSentinel</c>, which only <see cref="WritePlatformScopeAsync"/> may
    /// carry — is a caller error, not an outage, so nothing here reports it and the caller
    /// owes the alert.
    /// </para>
    /// </remarks>
    Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one SHOULD/MAY-class row, best effort. A failure is logged here and dropped; a
    /// draft refused before the write throws <see cref="ArgumentException"/>, as it does for
    /// <see cref="WriteStandaloneAsync"/>.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="WriteStandaloneAsync"/>, same two session variables, and
    /// the opposite failure posture. The accepted loss is written down in the module's
    /// coverage matrix rather than assumed.
    /// </remarks>
    Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a platform-scope row on the caller's own platform-role connection, before
    /// the operation it records runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One caller: <c>EnterPlatformAdminScope(reason)</c>. It writes <em>before</em> the
    /// operation so that an operation which later fails is still on the record — the
    /// entry into a cross-tenant scope is the fact worth keeping, whatever came of it.
    /// </para>
    /// <para>
    /// The connection and transaction are the scope's, not the request's:
    /// <c>learnstack_app</c> cannot write a row under the platform sentinel, and the
    /// request's transaction is the wrong lifetime for a record that must outlive it.
    /// </para>
    /// </remarks>
    Task WritePlatformScopeAsync(
        AuditEntryDraft entry,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default);
}

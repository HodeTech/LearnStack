namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// What kind of operation produced an audit row — the <c>operation_type</c> column.
/// </summary>
/// <remarks>
/// Seven members, matching § Operation Types of
/// <see href="../../../../docs/standards/18-audit-coverage.md">Audit Coverage
/// Standards</see>, which <c>OperationType_Enum_Matches_Catalog</c> asserts.
/// <see cref="PlatformAdmin"/> was split out of <see cref="Action"/> rather than
/// replacing it (ADR-0016, 2026-05-19): mapping a cross-tenant operator action to the
/// generic value loses the signal a regulator filters on.
/// </remarks>
public enum OperationType
{
    Create,
    Update,
    Delete,
    ReadSensitive,
    SecurityEvent,

    /// <summary>A platform admin acting against a tenant they are not a member of.</summary>
    PlatformAdmin,

    /// <summary>An in-tenant non-CRUD act that fits none of the above.</summary>
    Action,
}

/// <summary>
/// The coverage tier a module <em>declares</em> for an operation — the
/// <c>operation_class</c> column.
/// </summary>
/// <remarks>
/// This is what the catalogue and the module's matrix state. What
/// <c>IAuditConfigService.ClassifyAsync</c> <em>returns</em> is
/// <see cref="AuditClassification"/>, which is a different question: the declared tier
/// after the tenant's <c>audit_config</c> override and the MUST floor
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 3 § 4</see>).
/// </remarks>
public enum OperationClass
{
    May,
    Should,
    Must,
}

/// <summary>
/// The effective answer for one request, after the tenant override and the MUST floor.
/// </summary>
public enum AuditClassification
{
    /// <summary>Registered, and writes no row. How a test-only request type is declared.</summary>
    Off,

    May,
    Should,
    Must,

    /// <summary>
    /// The catalogue does not carry this request. The operation is <b>rejected</b> with
    /// <c>audit_unclassified_operation</c> — the fail-closed half of ADR-0033 that
    /// makes "proceeding unaudited" unreachable by construction.
    /// </summary>
    Unclassified,
}

/// <summary>
/// What became of the operation an audit row records — the <c>outcome</c> column.
/// </summary>
/// <remarks>
/// Four values, under a <c>CHECK</c>. ADR-0016's <c>is_success boolean</c> is
/// superseded: a boolean cannot carry <see cref="Denied"/>, which Audit Coverage
/// Standards requires in order to detect probing, and
/// <see cref="Indeterminate"/> is the commit-in-doubt row
/// (<see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// Amendment 2 § 3</see>).
/// </remarks>
public enum AuditOutcome
{
    Success,
    Denied,
    Failed,

    /// <summary>
    /// <c>COMMIT</c> faulted and the server-side outcome is unknown. The row is
    /// re-written standalone under the same <see cref="AuditEntryId"/> and a fresh
    /// timestamp; the pair is the recorded signature of a commit in doubt.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// Where a declared intent stands. The <b>only</b> durability signal in the system.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a "consumed" flag. <see cref="WrittenInTransaction"/> is not
/// durable — the transaction has not committed — and a per-request flag cannot observe
/// a rollback, because the flag lives in a DI scope and a rollback does not touch it.
/// Only <see cref="Committed"/> means the row is there.
/// </para>
/// <para>
/// Set by the <b>owning</b> unit-of-work frame and by nothing else
/// (<see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// Amendment 2 § 2</see>): a joiner's <c>CompleteAsync</c> is a documented no-op, so a
/// joiner reporting <see cref="Committed"/> would claim durability for a row nothing
/// has committed.
/// </para>
/// </remarks>
public enum AuditIntentState
{
    /// <summary>Nothing is declared for this request.</summary>
    None,

    /// <summary>Declared at step 3; nothing written.</summary>
    Pending,

    /// <summary><c>INSERT</c>ed on the ambient transaction — <b>not</b> yet durable.</summary>
    WrittenInTransaction,

    /// <summary>The ambient transaction committed. The row is durable.</summary>
    Committed,

    /// <summary>The ambient transaction rolled back. The row is gone.</summary>
    RolledBack,

    /// <summary><c>COMMIT</c> faulted; the server-side outcome is unknown.</summary>
    Indeterminate,
}

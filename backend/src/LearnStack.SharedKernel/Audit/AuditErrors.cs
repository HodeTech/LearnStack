using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// The two refusals the audit subsystem owns.
/// </summary>
/// <remarks>
/// Both are in the closed table of
/// <see href="../../../../docs/standards/09-error-handling.md">Standards 09</see> and in
/// <c>HttpStatusMap</c>, and they have to be in both: the status comes from the map and
/// the body's <c>code</c> comes from the key, so a code the map does not carry answers
/// <c>500</c> under a body that says otherwise — the one shape that contract forbids.
/// </remarks>
public static class AuditErrors
{
    /// <summary>
    /// A MUST-class row could not be written durably for an operation that would
    /// otherwise have <b>succeeded</b>. <c>503</c>.
    /// </summary>
    /// <remarks>
    /// Narrowed by
    /// <see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
    /// Amendment 1</see>: a standalone row recording an operation that was already being
    /// refused keeps its own <c>403</c> / <c>404</c>. Downgrading a refusal to a
    /// <c>503</c> an anonymous caller can provoke tells them more, not less.
    /// </remarks>
    public static Error Unavailable { get; } =
        new(new LocalizedMessage("lockey_audit_unavailable"));

    /// <summary>
    /// The catalogue does not classify this operation. <c>500</c>.
    /// </summary>
    /// <remarks>
    /// A deployment defect the caller can do nothing about, which is why it is a
    /// <c>500</c> and not a retryable <c>503</c>.
    /// <c>Every_TenantOwned_Command_HasAuditCoverage</c> exists to make it unreachable
    /// before a deployment, and this is what happens when it is reached anyway.
    /// </remarks>
    public static Error UnclassifiedOperation { get; } =
        new(new LocalizedMessage("lockey_audit_unclassified_operation"));
}

using LearnStack.SharedKernel.Errors;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// A MUST-class audit row could not be written durably.
/// </summary>
/// <remarks>
/// <para>
/// Thrown by <c>IAuditStore.WritePendingAsync</c>, caught by
/// <c>TransactionBehavior</c>, which rolls the business write back. It reaches the
/// client as <c>503 audit_unavailable</c> through
/// <c>HttpStatusMap.For(Exception)</c>'s existing <c>LearnStackException</c> branch —
/// so the fail-closed answer needs no change to <c>AuditLogBehavior</c>'s
/// catch-and-rethrow-through-<c>ExceptionDispatchInfo</c> contract, which
/// <see href="../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032</see>
/// binds.
/// </para>
/// <para>
/// It carries <see cref="AuditErrors.Unavailable"/> rather than
/// <c>InfrastructureException</c>'s default, because the default maps to
/// <c>dependency_unavailable</c> and a runbook needs to know which dependency.
/// </para>
/// </remarks>
public sealed class AuditWriteFailedException : InfrastructureException
{
    public AuditWriteFailedException(string message, Exception? innerException = null)
        : base(AuditErrors.Unavailable, message, innerException)
    {
    }
}

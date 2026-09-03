using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// A write an <see cref="IAggregateWriteStore{TRoot,TId}"/> could not perform because a
/// uniqueness the schema enforces already holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Part of the port's contract, not an implementation detail.</b> The alternative was
/// for a handler to catch the provider's own exception, and it is not available: the
/// repository forbids importing a provider SDK exception type outside the adapter's
/// namespace, and `Application` cannot reference the adapter's assembly in any case. An
/// adapter translates at the boundary; this is the type it translates to, so a handler
/// can answer a caller without ever naming a database.
/// </para>
/// <para>
/// <b>It is the caller's fault, not a fault.</b> A reused slug is an ordinary answer a
/// client can act on, and the carried <see cref="Error"/> — <c>business_rule_violation</c>
/// by default — is what makes an uncaught one a <c>409</c> rather than the <c>500</c> a
/// bare <c>DbUpdateException</c> produces, since neither it nor
/// <c>PostgresException</c> has an entry in <c>HttpStatusMap</c>.
/// </para>
/// <para>
/// <b>Catching it is the handler's job.</b> An uncaught one still reaches the L1
/// handler, which answers 409 correctly but also captures to
/// <c>IErrorTrackingProvider</c>, because <c>ShouldCapture</c> exempts only
/// <c>ProviderException.IsClientError</c> and a client-side <c>BadHttpRequestException</c>.
/// Adding a third arm is an edit to [ADR-0032](../../../../docs/decisions/0032-exception-handling-logging-and-observability.md)
/// § Sub-decision 7's table and is owed by the phase that first needs it —
/// [Phase 03](../../../../docs/roadmap/phase-03-identity-admin.md), which brings the
/// handlers that will use these ports in numbers. Today the one handler that can raise it
/// catches it.
/// </para>
/// </remarks>
public sealed class AggregateConflictException : LearnStackException
{
    private static readonly Error DefaultError = new(
        new LocalizedMessage("lockey_business_rule_violation"));

    public AggregateConflictException(
        string message, string? constraintName = null, Exception? innerException = null)
        : this(DefaultError, message, constraintName, innerException)
    {
    }

    public AggregateConflictException(
        Error error,
        string message,
        string? constraintName = null,
        Exception? innerException = null)
        : base(error, message, innerException) => ConstraintName = constraintName;

    /// <summary>
    /// The database constraint that refused the write, when the adapter knows it.
    /// </summary>
    /// <remarks>
    /// Carried because the two halves of one command fail for different reasons and a
    /// caller retrying blindly on the wrong one never succeeds: a taken slug needs a
    /// different slug, a duplicate id needs a different id. A handler maps it to a key;
    /// nothing outside a handler should read it.
    /// </remarks>
    public string? ConstraintName { get; }
}

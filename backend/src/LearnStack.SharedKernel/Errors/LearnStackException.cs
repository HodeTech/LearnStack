using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Errors;

/// <summary>
/// Base class for every exception LearnStack itself raises. Per
/// <see href="../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
/// § Sub-decision 4</see> and
/// <see href="../../../../docs/standards/09-error-handling.md">Standards 09 § Hierarchy</see>:
/// exceptions are reserved for <em>unexpected</em> failures (bugs, transient
/// infrastructure faults, contract violations). Expected outcomes return
/// <see cref="Result{T}"/> instead.
/// </summary>
/// <remarks>
/// Carrying the structured <see cref="Results.Error"/> at the exception site lets
/// the L1 <c>IExceptionHandler</c> map straight to RFC 7807 Problem Details
/// without re-deriving the code from the exception type. Subclasses pass the
/// appropriate stock <c>lockey_*</c>-keyed <see cref="Results.Error"/> through
/// their constructors.
/// </remarks>
public abstract class LearnStackException : Exception
{
    protected LearnStackException(Error error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>
    /// The stable <see cref="Results.Error"/> the L1 handler projects to the
    /// Problem Details body. <c>Error.Code</c> drives the HTTP status mapping
    /// (Standards 09 § Result Type).
    /// </summary>
    public Error Error { get; }
}

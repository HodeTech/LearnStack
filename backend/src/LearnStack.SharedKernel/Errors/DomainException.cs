using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Errors;

/// <summary>
/// Programmer-error exception. Raised <em>only</em> when an aggregate's
/// invariant is bypassed by a programming mistake — never for expected
/// business-rule violations (those return
/// <c>Result.Fail(business_rule_violation, …)</c> per
/// <see href="../../../docs/standards/09-error-handling.md">Standards 09 § Domain Exceptions</see>).
/// </summary>
/// <remarks>
/// The Roslyn analyzer <c>LearnStackException-DomainExceptionThrow</c> flags
/// every <c>throw new DomainException(...)</c> in <c>Domain</c> + <c>Application</c>
/// projects (Warning in Phase 02a, Error after Phase 03 exit). The companion
/// architecture test <c>Domain_Methods_Do_Not_Throw_For_Expected_Cases</c>
/// asserts the analyzer report is empty per module.
/// </remarks>
public sealed class DomainException : LearnStackException
{
    // A DomainException reaching the L1 handler is a *bug* — an invariant
    // was bypassed, not an expected outcome. Its default code must map to a
    // 500 (programmer / internal error), NOT business_rule_violation (409),
    // which is reserved for the Result.Fail path. Using
    // lockey_business_rule_violation here would misclassify a crash as a
    // refused business operation. Per ADR-0032 § Sub-decision 4 +
    // Standards 09 § Domain Exceptions.
    private static readonly Error DefaultError = new(
        new LocalizedMessage("lockey_internal_error"));

    public DomainException(string message, Exception? innerException = null)
        : base(DefaultError, message, innerException)
    {
    }

    public DomainException(Error error, string message, Exception? innerException = null)
        : base(error, message, innerException)
    {
    }
}

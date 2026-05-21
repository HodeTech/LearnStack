using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Errors;

/// <summary>
/// Thrown when application code requires a resolved tenant context but the
/// ambient <c>ITenantContext.IsResolved</c> is <c>false</c>. Reached only via
/// programmer-error paths — the request pipeline's <c>TenantContextBehavior</c>
/// asserts the context up front, so this exception escapes mainly from
/// background workers / outbox handlers that forgot to populate it.
/// </summary>
public sealed class TenantContextMissingException : LearnStackException
{
    private static readonly Error DefaultError = new(
        new LocalizedMessage("lockey_tenant_mismatch"));

    public TenantContextMissingException(string message, Exception? innerException = null)
        : base(DefaultError, message, innerException)
    {
    }
}

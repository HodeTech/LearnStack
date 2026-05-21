using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Errors;

/// <summary>
/// Transient infrastructure fault (database connection, Valkey, SeaweedFS,
/// outbox dispatcher transport). Retryable per Standards 09 § Retry vs Don't
/// Retry. Captured to <c>IErrorTrackingProvider</c> at the L1 handler.
/// </summary>
public class InfrastructureException : LearnStackException
{
    private static readonly Error DefaultError = new(
        new LocalizedMessage("lockey_dependency_unavailable"));

    public InfrastructureException(string message, Exception? innerException = null)
        : base(DefaultError, message, innerException)
    {
    }

    public InfrastructureException(Error error, string message, Exception? innerException = null)
        : base(error, message, innerException)
    {
    }
}

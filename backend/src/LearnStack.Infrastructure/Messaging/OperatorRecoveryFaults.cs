using Npgsql;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>
/// Which faults an operator call may report as unavailable. Only a fault the operator can wait out is one: a
/// refused credential, a missing grant or a missing table is a deployment defect, and answering it with a retryable
/// <c>503</c> would invite a retry loop that cannot succeed.
/// </summary>
public static class OperatorRecoveryFaults
{
    /// <summary>
    /// A database fault that Npgsql calls transient, a timeout, or the server's own statement timeout, which the
    /// dispatcher's role sets to the length of its bounded call and which Npgsql does not class as transient.
    /// </summary>
    public static bool IsTransientDatabaseFault(Exception failure, CancellationToken caller) => failure switch
    {
        PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } => true,
        NpgsqlException { IsTransient: true } => true,
        TimeoutException => true,
        OperationCanceledException => !caller.IsCancellationRequested,
        _ => false,
    };

    /// <summary>
    /// A transient database fault, or one of the owner's own refusals: the lane is busy, the pool is exhausted or the
    /// store is sealed by an unconfirmed cleanup, each of which is an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static bool IsUnavailable(Exception failure, CancellationToken caller) =>
        failure is InvalidOperationException || IsTransientDatabaseFault(failure, caller);
}

namespace LearnStack.SharedKernel.Idempotency;

/// <summary>Ends the request's business owner before a separate idempotency outcome owner starts.</summary>
/// <remarks>
/// True reports positively observed transaction and connection disposal. A detached
/// transaction, cancellation, timeout or repeated no-op disposal cannot establish it.
/// The caller awaits actual cleanup; false withholds success and prevents outcome-store work.
/// </remarks>
public interface IIdempotencyBusinessOwnerCleanup
{
    ValueTask<bool> EndBusinessOwnerAsync();
}

using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// A write an <see cref="IAggregateWriteStore{TRoot,TId}"/> could not perform
/// because the aggregate changed after it was read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The translation <see cref="IOptimisticConcurrency"/> already promises.</b> Its
/// remarks say a losing update "fails with <c>DbUpdateConcurrencyException</c> —
/// translated to a <c>Result.Fail(lockey_concurrency_conflict)</c>", and until the
/// first handler that could lose a race, nothing performed the translation: the
/// EF exception has no entry in <c>HttpStatusMap</c>, so the loser of an ordinary
/// race got a <c>500</c> for the one outcome optimistic concurrency exists to
/// report.
/// </para>
/// <para>
/// <b>A separate type from <see cref="AggregateConflictException"/>, because the
/// two ask the caller for different things.</b> A uniqueness violation means
/// "choose another value"; this means "re-read and try again", and it is the
/// difference between an answer a client can act on and one it cannot. They also
/// carry different codes — <c>business_rule_violation</c> and
/// <c>concurrency_conflict</c> — which is only visible if the types are distinct.
/// Both are <c>409</c>, and that is a coincidence of the status table rather than
/// a reason to fuse them.
/// </para>
/// <para>
/// It carries no constraint name: nothing was violated. What changed is the
/// concurrency token, and the row that holds it is the one the caller already
/// knows about.
/// </para>
/// </remarks>
public sealed class AggregateConcurrencyException : LearnStackException
{
    private static readonly Error ConcurrencyError = new(
        new LocalizedMessage("lockey_concurrency_conflict"));

    public AggregateConcurrencyException(string message, Exception? innerException = null)
        : base(ConcurrencyError, message, innerException)
    {
    }
}

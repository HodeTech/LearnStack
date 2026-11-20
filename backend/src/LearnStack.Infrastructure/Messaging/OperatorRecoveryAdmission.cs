using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>
/// Spends an operator's admission for one recovery transition: the operation it was minted for, still fresh, and
/// spent once (ADR-0101 § 7, ADR-0103 § 4).
/// </summary>
internal static class OperatorRecoveryAdmission
{
    /// <summary>Consumes <paramref name="admission"/>, or throws when it is not for <paramref name="expected"/>, is stale or spent.</summary>
    /// <exception cref="InvalidOperationException">The admission cannot be used for this transition.</exception>
    internal static void Consume(PlatformAdmission admission, PlatformOperation expected, IClock clock)
    {
        if (!ReferenceEquals(admission.Operation, expected)
            || string.IsNullOrEmpty(admission.Operator.CorrelationId)
            || (clock.UtcNow - admission.IssuedAt).Duration() > PlatformAdmission.MaximumAge
            || !admission.TryConsume())
        {
            throw new InvalidOperationException("The operator admission is not valid for this recovery.");
        }
    }
}

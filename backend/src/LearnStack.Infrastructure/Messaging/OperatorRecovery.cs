using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Platform;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>
/// The confined owner of the two fenced operator recovery transitions (ADR-0103 § 4): it holds no credential and no
/// SQL, and reaches the dispatcher's outbox role only through the two stores' own operator lane, behind an
/// <see cref="PlatformAdmission"/> the gate minted for exactly that operation.
/// </summary>
/// <remarks>
/// The route holds this interface and never a store, so no HTTP path resolves the dispatcher's store directly. The
/// owner counts every outcome so a replay storm and a fence refusal are visible to a monitoring owner.
/// </remarks>
public sealed class OperatorRecovery : IOperatorRecovery
{
    private readonly DeliveryDispatchStore _deliveries;
    private readonly OutboxDispatchStore _outbox;

    /// <summary>Builds the owner over the two stores it is the second writer of.</summary>
    public OperatorRecovery(DeliveryDispatchStore deliveries, OutboxDispatchStore outbox)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(outbox);
        _deliveries = deliveries;
        _outbox = outbox;
    }

    /// <inheritdoc />
    public async Task<OperatorRecoveryResult> ReplayDeliveryAsync(
        PlatformAdmission admission, DeliveryReplayRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return Counted(PlatformOperations.DeliveryReplay, await _deliveries.ReplayAsync(request, admission, cancellationToken));
        }
        catch (OperatorRecoveryUnavailableException failure)
        {
            PlatformOperatorMetrics.RecoveryUnavailable(PlatformOperations.DeliveryReplay, failure.CommitOutcomeUnknown);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<OperatorRecoveryResult> ResetOutboxAsync(
        PlatformAdmission admission, OutboxResetRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return Counted(PlatformOperations.DeliveryReset, await _outbox.ResetAsync(request, admission, cancellationToken));
        }
        catch (OperatorRecoveryUnavailableException failure)
        {
            PlatformOperatorMetrics.RecoveryUnavailable(PlatformOperations.DeliveryReset, failure.CommitOutcomeUnknown);
            throw;
        }
    }

    private static OperatorRecoveryResult Counted(PlatformOperation operation, OperatorRecoveryResult result)
    {
        PlatformOperatorMetrics.Recovery(operation, result.Outcome);
        if (result.Outcome == OperatorRecoveryOutcome.Applied)
        {
            PlatformOperatorMetrics.Result(operation);
        }

        return result;
    }
}

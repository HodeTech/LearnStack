using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Application.Messaging;

/// <summary>
/// Closed delivery completion seam. The production subtype and all installation/call
/// sites are enumerated by architecture tests; this is not a general precommit hook.
/// </summary>
public abstract class DeliveryCompletionParticipant
{
    private readonly IUnitOfWork _unitOfWork;

    protected DeliveryCompletionParticipant(IUnitOfWork unitOfWork, DeliveryCommitGate gate)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        Gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    internal DeliveryCommitGate Gate { get; }

    internal void RequireUnit(IUnitOfWork unitOfWork)
    {
        if (!ReferenceEquals(_unitOfWork, unitOfWork))
        {
            throw new InvalidOperationException("Delivery completion must share its prepared owner's unit.");
        }
    }

    internal Task FenceOwnerAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        RequireUnit(unitOfWork);
        return FenceAsync(cancellationToken);
    }

    /// <summary>Updates only this delivery's completion column on its bound live transaction.</summary>
    protected abstract Task FenceAsync(CancellationToken cancellationToken);
}

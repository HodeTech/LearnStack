using LearnStack.Infrastructure.Email;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Modules.Identity.Infrastructure.Invitations;

public sealed class InvitationSmtpOperation(InvitationSmtpDelivery delivery) : IInvitationEmailOperation
{
    public Task<BackgroundJobExternalResult<InvitationEmailOutcome>> ExecuteAsync(BackgroundJobExternalContext context,
        InvitationEmailPlan plan, CancellationToken cancellationToken = default) => ObserveAsync(context, plan, false, cancellationToken);
    public Task<BackgroundJobExternalResult<InvitationEmailOutcome>> ReconcileAsync(BackgroundJobExternalContext context,
        InvitationEmailPlan plan, CancellationToken cancellationToken = default) => ObserveAsync(context, plan, true, cancellationToken);
    private async Task<BackgroundJobExternalResult<InvitationEmailOutcome>> ObserveAsync(BackgroundJobExternalContext context,
        InvitationEmailPlan plan, bool recovery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var result = await delivery.ObserveAsync(context, plan.InvitationId, plan.GenerationId, plan.SpoolId, recovery, token).ConfigureAwait(false);
        if (result.ReceiptId is not { } receipt || result.EvidenceId is not { } evidence || result.Quiescence is not { } kind)
            return BackgroundJobExternalResult.Unknown<InvitationEmailOutcome>();
        var outcome = new InvitationEmailOutcome(receipt);
        var quiescence = new BackgroundJobQuiescenceEvidence(evidence, kind);
        return result.State switch
        {
            BackgroundJobExternalState.Succeeded => BackgroundJobExternalResult.Succeeded(outcome, quiescence),
            BackgroundJobExternalState.Failed => BackgroundJobExternalResult.Failed(outcome, quiescence, result.FailurePolicy!.Value),
            BackgroundJobExternalState.Cancelled => BackgroundJobExternalResult.Cancelled(outcome, quiescence, result.FailurePolicy!.Value),
            _ => BackgroundJobExternalResult.Unknown<InvitationEmailOutcome>(),
        };
    }
}

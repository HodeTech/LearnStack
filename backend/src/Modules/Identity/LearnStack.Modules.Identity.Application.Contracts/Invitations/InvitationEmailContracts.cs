using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Modules.Identity.Application.Contracts.Invitations;

public sealed record InvitationEmailParameters(Guid InvitationId, Guid GenerationId, Guid SpoolId) : IBackgroundJobParameters;
public sealed record InvitationEmailPlan(Guid InvitationId, Guid GenerationId, Guid SpoolId) : IBackgroundJobPlan;
public sealed record InvitationEmailOutcome(Guid ReceiptId) : IBackgroundJobOutcome;
public interface IInvitationEmailOperation : IBackgroundJobExternalOperation<InvitationEmailPlan, InvitationEmailOutcome> { }

using LearnStack.SharedKernel.Jobs;

namespace LearnStack.SharedKernel.Email;

public enum InvitationEmailDeliveryStatus { Queued, SubmissionAccepted, Failed, Unresolved, Unavailable }
/// <summary>Only the authorized invitation reader supplies its actual current job on its existing owner.</summary>
public interface IInvitationEmailStatusReader
{
    Task<InvitationEmailDeliveryStatus> ReadAsync(BackgroundJobId currentDeliveryJobId, CancellationToken cancellationToken = default);
}

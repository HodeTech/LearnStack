using LearnStack.SharedKernel.Jobs;

namespace LearnStack.SharedKernel.Email;

public enum InvitationEmailDeliveryStatus { Queued, SubmissionAccepted, Failed, Unresolved, Unavailable }
/// <summary>Only the authorized invitation reader supplies its actual current job on its existing owner.</summary>
public interface IInvitationEmailStatusReader
{
    Task<InvitationEmailDeliveryStatus> ReadAsync(BackgroundJobId currentDeliveryJobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the same reads in a single statement. Every requested job appears in the result; a job the
    /// announced owner cannot see is <see cref="InvitationEmailDeliveryStatus.Unavailable"/>, exactly as in
    /// <see cref="ReadAsync"/>. The caller supplies the loaded roots' actual current jobs and at most 100 of them.
    /// </summary>
    Task<IReadOnlyDictionary<BackgroundJobId, InvitationEmailDeliveryStatus>> ReadManyAsync(
        IReadOnlyCollection<BackgroundJobId> currentDeliveryJobIds, CancellationToken cancellationToken = default);
}

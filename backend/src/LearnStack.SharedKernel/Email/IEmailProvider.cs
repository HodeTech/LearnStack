namespace LearnStack.SharedKernel.Email;

/// <summary>A single SMTP submission; an accepted submission is not inbox delivery.</summary>
public interface IEmailProvider
{
    Task<EmailSubmissionObservation> SubmitAsync(
        EmailSubmission message,
        CancellationToken cancellationToken = default);
}

namespace LearnStack.SharedKernel.Email;

/// <summary>Closed transport evidence; contains no server response or exception text.</summary>
public sealed record EmailSubmissionObservation(EmailSubmissionState State, bool ActualCleanup)
{
    public override string ToString() => nameof(EmailSubmissionObservation);
}

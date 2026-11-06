namespace LearnStack.SharedKernel.Email;

public enum EmailSubmissionState
{
    Unknown = 0,
    Accepted = 1,
    TemporaryRejected = 2,
    PermanentRejected = 3,
}

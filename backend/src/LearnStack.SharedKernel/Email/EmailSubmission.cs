namespace LearnStack.SharedKernel.Email;

/// <summary>Immutable message content, without provider configuration or arbitrary headers.</summary>
public sealed record EmailSubmission(
    string Recipient,
    string Subject,
    string TextBody,
    string HtmlBody,
    string MessageId)
{
    public override string ToString() => nameof(EmailSubmission);
}

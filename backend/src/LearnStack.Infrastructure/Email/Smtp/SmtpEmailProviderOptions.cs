namespace LearnStack.Infrastructure.Email.Smtp;

/// <summary>Fixed process configuration. Composition alone grants Development plaintext.</summary>
public sealed record SmtpEmailProviderOptions(
    string Host,
    int Port,
    string SenderAddress,
    SmtpEmailTlsMode TlsMode,
    string? Username,
    string? Password,
    bool AllowDevelopmentPlaintext = false)
{
    public override string ToString() => nameof(SmtpEmailProviderOptions);
}

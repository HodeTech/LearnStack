namespace LearnStack.Infrastructure.Email.Smtp;

public enum SmtpEmailTlsMode
{
    RequiredStartTls = 0,
    ImplicitTls = 1,
    DevelopmentPlaintext = 2,
}

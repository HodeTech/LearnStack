using FluentAssertions;
using LearnStack.Infrastructure.Email.Smtp;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Resilience;
using Polly;
using Xunit;

namespace LearnStack.Tests.Unit;

public sealed class InvitationSmtpTransportValidationTests
{
    [Fact]
    public void Plaintext_Requires_Explicit_Development_Composition_Grant()
    {
        var options = new SmtpEmailProviderOptions("localhost", 25, "sender@example.test",
            SmtpEmailTlsMode.DevelopmentPlaintext, null, null);

        var act = () => new SmtpEmailProvider(options, new EmptyEmailResilience());

        act.Should().Throw<ArgumentException>().WithMessage("SMTP configuration is invalid.*");
    }

    [Fact]
    public void Production_Requires_Authentication()
    {
        var options = new SmtpEmailProviderOptions("localhost", 25, "sender@example.test",
            SmtpEmailTlsMode.RequiredStartTls, null, null);

        var act = () => new SmtpEmailProvider(options, new EmptyEmailResilience());

        act.Should().Throw<ArgumentException>().WithMessage("SMTP configuration is invalid.*");
    }

    [Theory]
    [InlineData("recipient@example.test\r\nBcc: intruder@example.test", "Invitation", "stable@example.test")]
    [InlineData("recipient@example.test", "Invitation\r\nBcc: intruder@example.test", "stable@example.test")]
    [InlineData("recipient@example.test", "Invitation", "stable@example.test\r\nX-Injection: value")]
    [InlineData("one@example.test,two@example.test", "Invitation", "stable@example.test")]
    [InlineData("name with-space@example.test", "Invitation", "stable@example.test")]
    [InlineData("\"quoted\"@example.test", "Invitation", "stable@example.test")]
    [InlineData("unicode\u00e9@example.test", "Invitation", "stable@example.test")]
    [InlineData("recipient@-invalid.test", "Invitation", "stable@example.test")]
    [InlineData("recipient@example..test", "Invitation", "stable@example.test")]
    [InlineData(".recipient@example.test", "Invitation", "stable@example.test")]
    public async Task Authored_Headers_Cannot_Inject_Another_Header_Or_Recipient(
        string recipient, string subject, string messageId)
    {
        var resilience = new EmptyEmailResilience();
        var provider = new SmtpEmailProvider(
            new("localhost", 25, "sender@example.test", SmtpEmailTlsMode.DevelopmentPlaintext,
                null, null, AllowDevelopmentPlaintext: true), resilience);

        var observation = await provider.SubmitAsync(new(recipient, subject, "text", "<p>html</p>", messageId));

        observation.Should().Be(new EmailSubmissionObservation(EmailSubmissionState.PermanentRejected, true));
        resilience.PipelineReads.Should().Be(0, "invalid content must be refused before any network callback");
    }

    [Fact]
    public void Sensitive_Values_Have_NameOnly_String_Representations()
    {
        var message = new EmailSubmission("recipient-canary@example.test", "subject-canary",
            "body-canary", "html-canary", "id-canary@example.test");
        var options = new SmtpEmailProviderOptions("host-canary.example.test", 465, "sender@example.test",
            SmtpEmailTlsMode.ImplicitTls, "user-canary", "password-canary");

        message.ToString().Should().Be(nameof(EmailSubmission));
        options.ToString().Should().Be(nameof(SmtpEmailProviderOptions));
        new EmailSubmissionObservation(EmailSubmissionState.Unknown, false).ToString()
            .Should().Be(nameof(EmailSubmissionObservation));
    }

    private sealed class EmptyEmailResilience : IProviderResilience<IEmailProvider>
    {
        public int PipelineReads { get; private set; }
        public ResiliencePipeline Pipeline
        {
            get
            {
                PipelineReads++;
                return ResiliencePipeline.Empty;
            }
        }

        public string PortName => "email";
    }
}

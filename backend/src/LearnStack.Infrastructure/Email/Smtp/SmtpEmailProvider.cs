using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Resilience;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Polly;

namespace LearnStack.Infrastructure.Email.Smtp;

/// <summary>
/// One physical SMTP submission with owned task/socket cleanup. ADR-0093 prohibits
/// transport retry; only the typed job may repeat a proven no-effect rejection.
/// </summary>
public sealed class SmtpEmailProvider : IEmailProvider
{
    private readonly SmtpEmailProviderOptions _options;
    private readonly IProviderResilience<IEmailProvider> _resilience;
    private readonly Func<SmtpClient> _createClient;
    private readonly object _cleanupGate = new();
    private readonly List<(SmtpClient? Client, MimeMessage? Message)> _retainedCleanup = [];

    public SmtpEmailProvider(
        SmtpEmailProviderOptions options,
        IProviderResilience<IEmailProvider> resilience)
        : this(options, resilience, static () => new SmtpClient())
    {
    }

    // Friend tests exercise real SDK cleanup failures; composition uses only the
    // public constructor and its fixed default client without a protocol logger.
    internal SmtpEmailProvider(
        SmtpEmailProviderOptions options,
        IProviderResilience<IEmailProvider> resilience,
        Func<SmtpClient> createClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resilience);
        ArgumentNullException.ThrowIfNull(createClient);
        ValidateOptions(options);
        _options = options;
        _resilience = resilience;
        _createClient = createClient;
    }

    public async Task<EmailSubmissionObservation> SubmitAsync(
        EmailSubmission message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_cleanupGate)
            if (_retainedCleanup.Count != 0) return new(EmailSubmissionState.Unknown, ActualCleanup: false);
        if (!IsValidMessage(message))
        {
            return new(EmailSubmissionState.PermanentRejected, ActualCleanup: true);
        }

        // Retain the actual work independently of the pipeline's finite wait.
        // A retrying/misconfigured collaborator can only await this SAME task.
        var gate = new object();
        Task<EmailSubmissionObservation>? actual = null;
        try
        {
            return await _resilience.Pipeline.ExecuteAsync(async token =>
            {
                Task<EmailSubmissionObservation> retained;
                lock (gate)
                {
                    actual ??= SubmitOnceAsync(message, token);
                    retained = actual;
                }

                var observation = await retained.ConfigureAwait(false);
                if (observation.State != EmailSubmissionState.Accepted)
                {
                    // Give the configured circuit breaker its canonical provider
                    // failure without embedding SDK text or an inner exception.
                    throw new ProviderException("smtp", "SMTP submission did not complete.",
                        isClientError: observation.State == EmailSubmissionState.PermanentRejected);
                }

                return observation;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No SDK/Polly exception, response or sensitive inner exception leaves
            // this boundary. Await positive actual cleanup even after cancellation.
            Task<EmailSubmissionObservation>? retained;
            lock (gate)
            {
                retained = actual;
            }

            return retained is null
                ? new(EmailSubmissionState.TemporaryRejected, ActualCleanup: true)
                : await retained.ConfigureAwait(false);
        }
    }

    private async Task<EmailSubmissionObservation> SubmitOnceAsync(
        EmailSubmission submission,
        CancellationToken cancellationToken)
    {
        SmtpClient? client = null;
        MimeMessage? message = null;
        var sendStarted = false;
        var state = EmailSubmissionState.Unknown;
        var actualCleanup = false;
        try
        {
            message = CreateMessage(submission);
            // The default client has no protocol logger. Never install one here.
            client = _createClient();
            await client.ConnectAsync(_options.Host, _options.Port, SocketOptions(), cancellationToken)
                .ConfigureAwait(false);

            if (_options.TlsMode != SmtpEmailTlsMode.DevelopmentPlaintext)
            {
                await client.AuthenticateAsync(_options.Username!, _options.Password!, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            sendStarted = true;
            // The result is server-authored text and is intentionally discarded.
            _ = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            state = EmailSubmissionState.Accepted;
        }
        catch (SmtpCommandException exception)
        {
            // Only an explicit negative SMTP reply establishes no acceptance.
            // Unexpected status, protocol loss and missing final reply stay Unknown.
            var negativeReply = !sendStarted || exception.ErrorCode is
                SmtpErrorCode.MessageNotAccepted or
                SmtpErrorCode.SenderNotAccepted or
                SmtpErrorCode.RecipientNotAccepted;
            var status = (int)exception.StatusCode;
            state = negativeReply && status is >= 400 and < 500
                ? EmailSubmissionState.TemporaryRejected
                : negativeReply && status is >= 500 and < 600
                    ? EmailSubmissionState.PermanentRejected
                    : EmailSubmissionState.Unknown;
        }
        catch (AuthenticationException)
        {
            state = EmailSubmissionState.PermanentRejected;
        }
        catch (Exception)
        {
            state = sendStarted ? EmailSubmissionState.Unknown : EmailSubmissionState.TemporaryRejected;
        }
        finally
        {
            try
            {
                // Dispose closes the underlying stream/socket synchronously after
                // the actual SendAsync has settled. QUIT is not submission evidence
                // and waiting for another server reply would add a cleanup dependency.
                client?.Dispose();
                message?.Dispose();
                actualCleanup = true;
            }
            catch (Exception)
            {
                // Keep the concrete socket/message reachable; the admitted source owner is retained too.
                lock (_cleanupGate) _retainedCleanup.Add((client, message));
                state = EmailSubmissionState.Unknown;
            }
        }

        return new(state, actualCleanup);
    }

    private MimeMessage CreateMessage(EmailSubmission submission)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.SenderAddress));
        message.To.Add(MailboxAddress.Parse(submission.Recipient));
        message.Subject = submission.Subject;
        message.MessageId = submission.MessageId;
        message.Body = new BodyBuilder
        {
            TextBody = submission.TextBody,
            HtmlBody = submission.HtmlBody,
        }.ToMessageBody();
        return message;
    }

    private SecureSocketOptions SocketOptions() => _options.TlsMode switch
    {
        SmtpEmailTlsMode.RequiredStartTls => SecureSocketOptions.StartTls,
        SmtpEmailTlsMode.ImplicitTls => SecureSocketOptions.SslOnConnect,
        SmtpEmailTlsMode.DevelopmentPlaintext => SecureSocketOptions.None,
        _ => throw new InvalidOperationException("SMTP configuration is invalid."),
    };

    private static bool IsValidMessage(EmailSubmission message) =>
        IsMailbox(message.Recipient) &&
        IsHeader(message.Subject, 256) &&
        IsMailbox(message.MessageId) &&
        !string.IsNullOrEmpty(message.TextBody) && message.TextBody.Length <= 65_536 &&
        !string.IsNullOrEmpty(message.HtmlBody) && message.HtmlBody.Length <= 65_536;

    private static bool IsMailbox(string? value)
    {
        if (value is null || !IsHeader(value, 254) || value.Any(character => character > 127 || char.IsWhiteSpace(character)))
        {
            return false;
        }

        var separator = value.IndexOf('@', StringComparison.Ordinal);
        if (separator is < 1 or > 64 || separator != value.LastIndexOf('@') || separator == value.Length - 1)
        {
            return false;
        }

        var local = value.AsSpan(0, separator);
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in local)
        {
            if (!char.IsAsciiLetterOrDigit(character) && !".!#$%&'*+-/=?^_`{|}~".Contains(character))
            {
                return false;
            }
        }

        var domain = value[(separator + 1)..];
        return domain.Split('.').All(label => label.Length is >= 1 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static bool IsHeader(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static void ValidateOptions(SmtpEmailProviderOptions options)
    {
        var plaintext = options.TlsMode == SmtpEmailTlsMode.DevelopmentPlaintext;
        if (!IsHeader(options.Host, 253) || Uri.CheckHostName(options.Host) == UriHostNameType.Unknown ||
            options.Port is < 1 or > 65_535 || !IsMailbox(options.SenderAddress) ||
            !Enum.IsDefined(options.TlsMode) ||
            (plaintext && (!options.AllowDevelopmentPlaintext ||
                options.Username is not null || options.Password is not null)) ||
            (!plaintext && (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrEmpty(options.Password))))
        {
            throw new ArgumentException("SMTP configuration is invalid.", nameof(options));
        }
    }
}

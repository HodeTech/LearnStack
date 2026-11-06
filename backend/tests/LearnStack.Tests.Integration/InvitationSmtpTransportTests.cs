using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using FluentAssertions;
using LearnStack.Infrastructure.Email.Smtp;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Resilience;
using MailKit.Net.Smtp;
using MimeKit;
using Polly;
using Polly.Retry;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed class InvitationSmtpTransportTests
{
    [Fact]
    public async Task Final250_Reports_SubmissionAccepted_Only_After_SocketCleanup()
    {
        await using var server = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        var provider = CreateProvider(server.Port);

        var observation = await provider.SubmitAsync(Message());

        observation.Should().Be(new EmailSubmissionObservation(EmailSubmissionState.Accepted, true));
        await server.ClientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.Connections.Should().Be(1);
        server.Messages.Should().Be(1);
        server.MessageId.Should().Be("<invitation-transport-test@example.test>");
    }

    [Fact]
    public async Task Final250_Followed_By_Server_Close_Preserves_Acceptance_After_ActualCleanup()
    {
        await using var server = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.AcceptThenClose);

        var observation = await CreateProvider(server.Port).SubmitAsync(Message());

        // A peer which closes immediately after its final 250 cannot serve QUIT.
        // Actual local disposal still completes; the known submission survives it.
        observation.Should().Be(new EmailSubmissionObservation(EmailSubmissionState.Accepted, true));
        server.Connections.Should().Be(1);
        server.Messages.Should().Be(1);
    }

    [Fact]
    public async Task Disposal_Failure_Retains_The_Actual_Connected_Client_And_Sent_Message_And_Refuses_New_Work()
    {
        await using var server = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        var client = new InvitationSmtpTransportDisposalFailureClient();
        MimeMessage? sentMessage = null;
        client.MessageSent += (_, sent) => sentMessage = sent.Message;
        var creations = 0;
        var provider = new SmtpEmailProvider(
            new("127.0.0.1", server.Port, "sender@example.test", SmtpEmailTlsMode.DevelopmentPlaintext,
                null, null, AllowDevelopmentPlaintext: true),
            new InvitationSmtpTransportResilience(ResiliencePipeline.Empty),
            () =>
            {
                creations++;
                return client;
            });
        try
        {
            var observation = await provider.SubmitAsync(Message());

            observation.Should().Be(new EmailSubmissionObservation(EmailSubmissionState.Unknown, false));
            client.DisposalAttempts.Should().Be(1);
            client.IsConnected.Should().BeTrue("the deliberate disposal failure occurs before actual socket cleanup");
            server.ClientClosed.Task.IsCompleted.Should().BeFalse();
            sentMessage.Should().NotBeNull("the real SDK observed the peer's final 250 before disposal failed");
            var field = typeof(SmtpEmailProvider).GetField("_retainedCleanup", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var retained = (List<(SmtpClient? Client, MimeMessage? Message)>)field.GetValue(provider)!;
            retained.Should().ContainSingle();
            retained[0].Client.Should().BeSameAs(client);
            retained[0].Message.Should().BeSameAs(sentMessage);

            var later = await provider.SubmitAsync(Message());

            later.Should().Be(new EmailSubmissionObservation(EmailSubmissionState.Unknown, false));
            creations.Should().Be(1);
            server.Connections.Should().Be(1);
            server.Messages.Should().Be(1);
        }
        finally
        {
            // The fixture alone repairs/disposes its own injected client. The
            // production owner never treats this failed disposal as quiescence.
            client.FailDisposal = false;
            client.Dispose();
            sentMessage?.Dispose();
        }

        await server.ClientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(InvitationSmtpTransportBehavior.TemporaryRecipientRejection, EmailSubmissionState.TemporaryRejected)]
    [InlineData(InvitationSmtpTransportBehavior.PermanentRecipientRejection, EmailSubmissionState.PermanentRejected)]
    public async Task Explicit_Recipient_Rejection_Proves_NoAcceptance(
        InvitationSmtpTransportBehavior behavior,
        EmailSubmissionState expected)
    {
        await using var server = new InvitationSmtpTransportServer(behavior);

        var observation = await CreateProvider(server.Port).SubmitAsync(Message());

        observation.Should().Be(new EmailSubmissionObservation(expected, true));
        await server.ClientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.Connections.Should().Be(1);
        server.Messages.Should().Be(0);
        observation.ToString().Should().Be(nameof(EmailSubmissionObservation));
    }

    [Fact]
    public async Task Lost_FinalReply_Remains_Unknown_Without_HiddenRetry()
    {
        await using var server = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.DropFinalReply);

        var observation = await CreateProvider(server.Port).SubmitAsync(Message());

        observation.Should().Be(new EmailSubmissionObservation(EmailSubmissionState.Unknown, true));
        server.Connections.Should().Be(1);
        server.Messages.Should().Be(1);
    }

    [Fact]
    public async Task Provider_Response_Text_Does_Not_Enter_Sdk_Activities_Or_Observation()
    {
        var diagnostics = new ConcurrentBag<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MailKit.Net.SmtpClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                diagnostics.Add(activity.StatusDescription ?? string.Empty);
                foreach (var tag in activity.TagObjects)
                {
                    diagnostics.Add(Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                }

                foreach (var entry in activity.Events)
                {
                    diagnostics.Add(entry.Name);
                    foreach (var tag in entry.Tags)
                    {
                        diagnostics.Add(Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var server = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.PermanentRecipientRejection);

        var observation = await CreateProvider(server.Port).SubmitAsync(Message());

        observation.State.Should().Be(EmailSubmissionState.PermanentRejected);
        diagnostics.Should().NotBeEmpty("the real pinned SDK must be exercised with its activities subscribed");
        diagnostics.Should().NotContain(value => value.Contains("response-canary-never-expose", StringComparison.Ordinal));
        observation.ToString().Should().Be(nameof(EmailSubmissionObservation));
    }

    [Fact]
    public async Task Cancellation_After_Data_Awaits_Actual_SocketCleanup_And_Remains_Unknown()
    {
        await using var server = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.HoldFinalReply);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var submission = CreateProvider(server.Port).SubmitAsync(Message(), cancellation.Token);
        await server.DataReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();
        var observation = await submission.WaitAsync(TimeSpan.FromSeconds(5));

        observation.Should().Be(new EmailSubmissionObservation(EmailSubmissionState.Unknown, true));
        await server.ClientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.Connections.Should().Be(1);
        server.Messages.Should().Be(1);
    }

    [Fact]
    public async Task Even_Retrying_Collaborator_Cannot_Repeat_Physical_Submission()
    {
        await using var server = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.DropFinalReply);
        var attempts = 0;
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = _ => ValueTask.FromResult(true),
                OnRetry = _ =>
                {
                    Interlocked.Increment(ref attempts);
                    return ValueTask.CompletedTask;
                },
            }).Build();

        var observation = await CreateProvider(server.Port, pipeline).SubmitAsync(Message());

        observation.State.Should().Be(EmailSubmissionState.Unknown);
        observation.ActualCleanup.Should().BeTrue();
        attempts.Should().Be(2);
        server.Connections.Should().Be(1);
        server.Messages.Should().Be(1);
    }

    [Fact]
    public async Task Required_StartTls_Cannot_Downgrade_To_Plaintext()
    {
        await using var server = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        var provider = new SmtpEmailProvider(
            new("127.0.0.1", server.Port, "sender@example.test", SmtpEmailTlsMode.RequiredStartTls, "user", "password"),
            new InvitationSmtpTransportResilience(ResiliencePipeline.Empty));

        var observation = await provider.SubmitAsync(Message());

        observation.State.Should().NotBe(EmailSubmissionState.Accepted);
        observation.ActualCleanup.Should().BeTrue();
        await server.ClientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.Messages.Should().Be(0);
    }

    private static SmtpEmailProvider CreateProvider(int port, ResiliencePipeline? pipeline = null) => new(
        new("127.0.0.1", port, "sender@example.test", SmtpEmailTlsMode.DevelopmentPlaintext,
            null, null, AllowDevelopmentPlaintext: true),
        new InvitationSmtpTransportResilience(pipeline ?? ResiliencePipeline.Empty));

    private static EmailSubmission Message() => new(
        "recipient@example.test", "Invitation", "Text invitation", "<p>HTML invitation</p>",
        "invitation-transport-test@example.test");

    private sealed class InvitationSmtpTransportDisposalFailureClient : SmtpClient
    {
        public bool FailDisposal { get; set; } = true;
        public int DisposalAttempts { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && FailDisposal)
            {
                DisposalAttempts++;
                throw new InvalidOperationException("Owned SMTP fixture disposal failed.");
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed class InvitationSmtpTransportResilience(ResiliencePipeline pipeline) : IProviderResilience<IEmailProvider>
{
    public ResiliencePipeline Pipeline { get; } = pipeline;
    public string PortName => "email";
}

/// <summary>A fresh loopback SMTP peer; never discovers or touches workstation services.</summary>
internal sealed class InvitationSmtpTransportServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly Task _owner;
    private readonly InvitationSmtpTransportBehavior _behavior;
    private readonly List<Task> _clients = [];
    private int _connections;
    private int _messages;

    public InvitationSmtpTransportServer(InvitationSmtpTransportBehavior behavior, TimeSpan? lifetime = null)
    {
        _stop = new(lifetime ?? TimeSpan.FromSeconds(30));
        _behavior = behavior;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _owner = AcceptAsync();
    }

    public int Port { get; }
    public int Connections => Volatile.Read(ref _connections);
    public int Messages => Volatile.Read(ref _messages);
    public string? MessageId { get; private set; }
    public TaskCompletionSource DataReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ClientClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                _clients.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(_clients);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true,
            };
            try
            {
                await writer.WriteLineAsync("220 owned-test-peer SMTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 owned-test-peer");
                    }
                    else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 sender accepted");
                    }
                    else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                    {
                        var code = _behavior switch
                        {
                            InvitationSmtpTransportBehavior.TemporaryRecipientRejection => 450,
                            InvitationSmtpTransportBehavior.PermanentRecipientRejection => 550,
                            _ => 250,
                        };
                        await writer.WriteLineAsync(code.ToString(CultureInfo.InvariantCulture) + " response-canary-never-expose");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("354 message follows");
                        while (await reader.ReadLineAsync(_stop.Token) is { } bodyLine && bodyLine != ".")
                        {
                            if (bodyLine.StartsWith("Message-Id: ", StringComparison.OrdinalIgnoreCase))
                            {
                                MessageId = bodyLine[12..];
                            }
                        }

                        Interlocked.Increment(ref _messages);
                        DataReceived.TrySetResult();
                        if (_behavior == InvitationSmtpTransportBehavior.DropFinalReply)
                        {
                            return;
                        }

                        if (_behavior != InvitationSmtpTransportBehavior.HoldFinalReply)
                        {
                            await writer.WriteLineAsync("250 accepted-canary-never-expose");
                            if (_behavior == InvitationSmtpTransportBehavior.AcceptThenClose)
                            {
                                return;
                            }
                        }
                    }
                    else if (line.Equals("RSET", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 reset");
                    }
                    else
                    {
                        await writer.WriteLineAsync("500 unsupported command");
                    }
                }

                ClientClosed.TrySetResult();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _owner;
        _listener.Stop();
        _stop.Dispose();
    }
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class KeycloakFixture
{
    private bool _invitationMailEnabled;
    private IContainer? _invitationMail;
    private HttpClient? _invitationMailApi;
    private readonly HashSet<string> _readInvitationMessages = new(StringComparer.Ordinal);

    internal int InvitationSmtpPort => _invitationMail?.GetMappedPublicPort(1025)
        ?? throw new InvalidOperationException("Owned invitation mail is unavailable.");

    internal static async Task<KeycloakFixture> StartWithInvitationMailAsync()
    {
        var fixture = new KeycloakFixture { _invitationMailEnabled = true };
        await fixture.InitializeAsync();
        return fixture;
    }

    private async Task StartInvitationMailAsync(CancellationToken cancellationToken)
    {
        if (!_invitationMailEnabled) return;
        if (_network is null) throw new InvalidOperationException("Owned invitation network is unavailable.");
        _invitationMail = new ContainerBuilder("axllent/mailpit:v1.29.7")
            .WithLogger(NullLogger.Instance)
            .WithNetwork(_network)
            .WithNetworkAliases("invitation-mail")
            .WithPortBinding(8025, true)
            .WithPortBinding(1025, true)
            .WithCreateParameterModifier(parameters =>
            {
                var bindings = parameters.HostConfig?.PortBindings;
                if (bindings is not { Count: 2 } || !bindings.ContainsKey("8025/tcp") || !bindings.ContainsKey("1025/tcp")
                    || bindings.Values.Any(values => values.Count != 1))
                    throw new InvalidOperationException("Owned invitation mail port inventory is invalid.");
                foreach (var values in bindings.Values) values[0].HostIP = "127.0.0.1";
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(8025).ForPath("/readyz").ForStatusCode(HttpStatusCode.OK)))
            .Build();
        await _invitationMail.StartAsync(cancellationToken);
        var address = new UriBuilder("http", _invitationMail.Hostname, _invitationMail.GetMappedPublicPort(8025)).Uri;
        if (!address.IsLoopback) throw new InvalidOperationException("Owned invitation mail is not loopback.");
        _invitationMailApi = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 256 * 1024 };
    }

    private string ConfigureInvitationMailRealm(string source)
    {
        if (!_invitationMailEnabled) return source;
        var realm = JsonNode.Parse(source)?.AsObject()
            ?? throw new InvalidOperationException("Owned invitation realm is invalid.");
        realm["smtpServer"] = new JsonObject
        {
            ["host"] = "invitation-mail",
            ["port"] = "1025",
            ["from"] = "verification@example.test",
            ["fromDisplayName"] = "Owned verification fixture",
            ["auth"] = "false",
            ["ssl"] = "false",
            ["starttls"] = "false",
        };
        return realm.ToJsonString();
    }

    private async Task AssertInvitationMailBindingsAsync(CancellationToken cancellationToken)
    {
        if (_invitationMail is null) return;
        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
        var actual = await docker.Containers.InspectContainerAsync(_invitationMail.Id, cancellationToken);
        var ports = new HashSet<string>(["8025/tcp", "1025/tcp"], StringComparer.Ordinal);
        if (actual.HostConfig?.PortBindings is not { Count: 2 } configured || !ports.SetEquals(configured.Keys)
            || configured.Values.Any(values => values.Count != 1 || values[0].HostIP != "127.0.0.1")
            || actual.NetworkSettings?.Ports is not { } published
            || published.Any(pair => !ports.Contains(pair.Key) && pair.Value is not (null or { Count: 0 }))
            || ports.Any(port => !published.TryGetValue(port, out var values) || values is not { Count: 1 }
                || values[0].HostIP != "127.0.0.1" || !ushort.TryParse(values[0].HostPort, out var mapped) || mapped == 0))
            throw new InvalidOperationException("Owned invitation mail ports are not exclusively loopback.");
    }

    // Private mailbox reads are bounded and never enter test output or assertion values.
    internal async Task<string> ReadInvitationMailAsync(string recipient, string expectedPath, CancellationToken cancellationToken)
    {
        if (_invitationMailApi is null || expectedPath is not ("/invitations" or "/realms/learnstack/login-actions/action-token"))
            throw new InvalidOperationException("Owned invitation mail read is invalid.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                using var listing = await KeycloakHttp.JsonAsync(_invitationMailApi, HttpMethod.Get, "api/v1/messages?limit=50", cancellationToken: deadline.Token);
                if (!listing.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() > 50)
                    throw new InvalidOperationException("Owned invitation mailbox response is invalid.");
                foreach (var summary in messages.EnumerateArray())
                {
                    if (!summary.TryGetProperty("ID", out var idValue) || idValue.GetString() is not { Length: > 0 and <= 100 } id
                        || !Regex.IsMatch(id, "^[A-Za-z0-9-]+$") || _readInvitationMessages.Contains(id)
                        || !summary.TryGetProperty("To", out var recipients) || recipients.ValueKind != JsonValueKind.Array
                        || !recipients.EnumerateArray().Any(address => address.TryGetProperty("Address", out var value) && value.GetString() == recipient)) continue;
                    using var message = await KeycloakHttp.JsonAsync(_invitationMailApi, HttpMethod.Get, "api/v1/message/" + id, cancellationToken: deadline.Token);
                    var text = message.RootElement.TryGetProperty("Text", out var plain) ? plain.GetString() : null;
                    if (text is null || text.Length > 64 * 1024) throw new InvalidOperationException("Owned invitation mail body is invalid.");
                    foreach (Match candidate in Regex.Matches(text, @"https://[^\s<>""']+"))
                    {
                        if (!Uri.TryCreate(candidate.Value, UriKind.Absolute, out var link) || link.AbsolutePath != expectedPath) continue;
                        if (expectedPath == "/realms/learnstack/login-actions/action-token"
                            && link.GetLeftPart(UriPartial.Authority) != BaseAddress.GetLeftPart(UriPartial.Authority))
                            throw new InvalidOperationException("Owned verification link escaped its provider.");
                        _readInvitationMessages.Add(id);
                        return link.AbsoluteUri;
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("Owned invitation mail did not arrive within its deadline."); }
    }

    private async Task DisposeInvitationMailAsync()
    {
        _invitationMailApi?.Dispose();
        if (_invitationMail is not null) await _invitationMail.DisposeAsync();
    }
}

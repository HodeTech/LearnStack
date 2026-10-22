using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Offline transport-owner controls, with no provider or Docker dependency.</summary>
public sealed class KeycloakTransportTests
{
    [Fact]
    public void Startup_failure_diagnostics_distinguish_closed_Docker_faults_without_provider_text()
    {
        const string canary = "private-provider-response-canary";
        var statuses = new[]
        {
            (HttpStatusCode.BadRequest, "docker_bad_request"),
            (HttpStatusCode.Unauthorized, "docker_access_denied"),
            (HttpStatusCode.Forbidden, "docker_access_denied"),
            (HttpStatusCode.NotFound, "docker_not_found"),
            (HttpStatusCode.Conflict, "docker_conflict"),
            (HttpStatusCode.TooManyRequests, "docker_rate_limited"),
            (HttpStatusCode.InternalServerError, "docker_server_error"),
            (HttpStatusCode.BadGateway, "docker_api_error"),
            ((HttpStatusCode)999, "docker_api_error"),
        };
        foreach (var (status, reason) in statuses)
        {
            var exception = new Docker.DotNet.DockerApiException(status, canary);
            exception.Message.Should().Contain(canary, "the control must contain raw provider text");
            KeycloakStartupFailure.Classify(exception).Should().Be(reason).And.NotContain(canary);
        }

        const string network = "failed to set up container networking: ";
        const string privateDetail = canary + " 127.0.0.1:5432 /private/provider/path Private.Provider.Type";
        static string Body(string message) => JsonSerializer.Serialize(new { message });
        var daemonMessages = new[]
        {
            (network + "driver failed programming external connectivity on endpoint " + privateDetail
                + ": Bind for " + privateDetail + " failed: port is already allocated", "docker_port_allocated"),
            (network + "failed to bind host port for " + privateDetail, "docker_port_bind"),
            (network + "failed to bind host port 5432 for " + privateDetail, "docker_port_bind"),
            (network + "failed to listen on TCP socket: " + privateDetail, "docker_port_listen"),
            (network + "failed to start userland proxy for port mapping " + privateDetail, "docker_port_proxy"),
            (network + "failed to bind host port for " + privateDetail
                + ": failed to listen on TCP socket: " + privateDetail, "docker_port_listen"),
            (network + privateDetail, "docker_network_error"),
            ("failed to create task for container: " + privateDetail, "docker_runtime_task"),
        };
        foreach (var (message, reason) in daemonMessages)
        {
            var body = Body(message);
            var exception = new Docker.DotNet.DockerApiException(HttpStatusCode.InternalServerError, body);
            exception.ResponseBody.Should().Contain(canary, "the control must contain private daemon text");
            KeycloakStartupFailure.Classify(exception).Should().Be(reason).And.NotContain(canary);
            KeycloakStartupFailure.Classify(new Docker.DotNet.DockerApiException(HttpStatusCode.BadRequest, body))
                .Should().Be("docker_bad_request", "body parsing is confined to HTTP 500");
            KeycloakStartupFailure.Classify(new Docker.DotNet.DockerApiException(HttpStatusCode.BadGateway, body))
                .Should().Be("docker_api_error", "body parsing is confined to HTTP 500");
        }

        var bounded = Body(network + privateDetail);
        bounded = bounded.PadRight(16384);
        KeycloakStartupFailure.Classify(new Docker.DotNet.DockerApiException(HttpStatusCode.InternalServerError, bounded))
            .Should().Be("docker_network_error", "the exact body limit remains valid JSON");
        KeycloakStartupFailure.Classify(new Docker.DotNet.DockerApiException(HttpStatusCode.InternalServerError, null!))
            .Should().Be("docker_server_error");
        var unclassifiedBodies = new[]
        {
            "", canary, network + "failed to bind host port " + privateDetail,
            "{}", "null", "[]", "{\"message\":null}", "{\"message\":1}", "{\"message\":{}}",
            "{\"message\":[]}", "{\"Message\":\"" + network + "\"}",
            "{\"error\":{\"message\":\"" + network + "\"}}",
            "{\"message\":\"" + network + "\",\"extra\":[]}",
            "{\"message\":\"" + network + "\",\"extra\":1}",
            "{\"extra\":null,\"message\":\"" + network + "\"}",
            "{\"message\":\"" + network + "\",\"message\":\"" + network + "\"}",
            "{\"message\":\"" + network + "\",\"message\":\"different\"}",
            "{\"message\":\"" + network + "\",\"messa\\u0067e\":\"" + network + "\"}",
            "{\"message\":\"" + network + "\",}", "/* comment */" + Body(network),
            "{\"message\":\"\\uD800\"}", "{\"messa\\uD800ge\":\"" + network + "\"}",
            "{\"message\":\"\uD800\"}", "{\"message\":\"\uDC00\"}",
            "{\"messa\uD800ge\":\"" + network + "\"}", "{\"messa\uDC00ge\":\"" + network + "\"}",
            Body(network) + "{}", "{\"message\":\"unterminated", bounded + " ",
            "{\"nested\":" + new string('[', 64) + "0" + new string(']', 64) + "}",
            Body("failed: port is already allocated " + privateDetail),
            Body("failed to bind host port " + privateDetail),
            Body("failed to listen on TCP socket: " + privateDetail),
            Body("failed to start userland proxy for port mapping " + privateDetail),
            Body(privateDetail + " " + network), Body(" " + network),
            Body("Failed to set up container networking: " + privateDetail),
            Body(privateDetail + " failed to create task for container:"),
            Body(network + privateDetail + "; " + network + "failed to bind host port " + privateDetail),
            Body("failed to create task for container: " + privateDetail + "; failed to create task for container: " + privateDetail),
            Body("permission denied /sys/fs/cgroup " + privateDetail),
            Body("no space left on device " + privateDetail),
            Body("docker_port_bind " + privateDetail),
        };
        foreach (var body in unclassifiedBodies)
            KeycloakStartupFailure.Classify(new Docker.DotNet.DockerApiException(HttpStatusCode.InternalServerError, body))
                .Should().Be("docker_server_error").And.NotContain(canary);
        foreach (var message in new[]
        {
            network + "port is already allocated " + privateDetail,
            network + "failed to bind host ports " + privateDetail,
            network + "failed to listen on TCP socket " + privateDetail,
            network + "failed to start userland proxy " + privateDetail,
            network + "permission denied /sys/fs/cgroup " + privateDetail,
        })
            KeycloakStartupFailure.Classify(new Docker.DotNet.DockerApiException(HttpStatusCode.InternalServerError, Body(message)))
                .Should().Be("docker_network_error", "only the verified outer wrapper is recognized");

        KeycloakStartupFailure.Classify(new DotNet.Testcontainers.Containers.ResourceReaperException(canary))
            .Should().Be("resource_reaper");
        KeycloakStartupFailure.Classify(new UnauthorizedAccessException(canary))
            .Should().Be("filesystem_access_denied");
        KeycloakStartupFailure.Classify(new IOException(canary)).Should().Be("filesystem_io");
        KeycloakStartupFailure.Classify(new System.Net.Sockets.SocketException())
            .Should().Be("socket_error");
        foreach (var message in new[] { canary, "docker_conflict", "resource_reaper", "deadline" })
            KeycloakStartupFailure.Classify(new FormatException(message)).Should().Be("provider_error",
                "unclassified provider text cannot select a diagnostic reason");
    }

    [Fact]
    public async Task Caller_cancellation_reaches_inflight_transport_and_retains_cancellation_identity()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json();
        }))
        { BaseAddress = new Uri("https://fixture.invalid/") };
        using var cancellation = new CancellationTokenSource();
        var pending = KeycloakHttp.SendAsync(client, HttpMethod.Get, "metadata", cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var act = async () => { using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5)); };
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Transport_failure_is_closed_and_compressed_response_is_refused()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("credential-url-canary")))
        { BaseAddress = new Uri("https://fixture.invalid/") };
        var act = async () => { using var response = await KeycloakHttp.SendAsync(client, HttpMethod.Get, "metadata"); };
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.ToString().Should().NotContain("credential-url-canary");
        using var compressed = new HttpClient(new Handler((_, _) =>
        {
            var response = Json();
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        }))
        { BaseAddress = new Uri("https://fixture.invalid/") };
        var encoded = async () => { using var response = await KeycloakHttp.SendAsync(compressed, HttpMethod.Get, "metadata"); };
        await encoded.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Administrator_refresh_precedes_expiry_and_never_replays_a_refused_mutation()
    {
        var clock = new Clock();
        var credentials = 0;
        var sent = new List<string?>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            sent.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(request.Method == HttpMethod.Put
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json());
        }))
        { BaseAddress = new Uri("https://fixture.invalid/") };
        using var admin = new KeycloakAdminClient(http, _ => Task.FromResult(new KeycloakAdminCredential(
            "credential-" + ++credentials, TimeSpan.FromSeconds(60))), clock);
        await admin.GetAsync();
        clock.Advance(TimeSpan.FromSeconds(54));
        await admin.GetAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        (await admin.PutAsync("realm", new { value = 1 })).Should().Be(HttpStatusCode.Unauthorized);
        credentials.Should().Be(2, "freshness is checked before the first request which reaches the expiry margin");
        sent.Should().Equal("credential-1", "credential-1", "credential-2");
    }

    private static HttpResponseMessage Json() => new(HttpStatusCode.OK)
    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan interval) => _ticks += interval.Ticks;
    }
}

using System.Net;
using System.Text;
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
            KeycloakFixture.StartupFailureReason(exception).Should().Be(reason).And.NotContain(canary);
        }

        KeycloakFixture.StartupFailureReason(new DotNet.Testcontainers.Containers.ResourceReaperException(canary))
            .Should().Be("resource_reaper");
        KeycloakFixture.StartupFailureReason(new UnauthorizedAccessException(canary))
            .Should().Be("filesystem_access_denied");
        KeycloakFixture.StartupFailureReason(new IOException(canary)).Should().Be("filesystem_io");
        KeycloakFixture.StartupFailureReason(new System.Net.Sockets.SocketException())
            .Should().Be("socket_error");
        foreach (var message in new[] { canary, "docker_conflict", "resource_reaper", "deadline" })
            KeycloakFixture.StartupFailureReason(new FormatException(message)).Should().Be("provider_error",
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

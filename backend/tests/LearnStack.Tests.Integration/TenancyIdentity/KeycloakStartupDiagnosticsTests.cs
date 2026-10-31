using System.Net;
using System.Security.Authentication;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Offline observations cannot change the readiness request, result or exception.</summary>
public sealed class KeycloakStartupDiagnosticsTests
{
    [Fact]
    public void Snapshot_freezes_stage_times_and_bounds_elapsed_context_without_a_wall_clock()
    {
        var clock = new Clock();
        var diagnostics = new KeycloakStartupDiagnostics(clock);
        diagnostics.Mark("certificate");
        clock.Advance(TimeSpan.FromMilliseconds(125));
        diagnostics.Mark("network");
        diagnostics.Mark("private-stage-canary");
        var first = diagnostics.Snapshot(null);
        first.FixtureOrdinal.Should().BeInRange(1, KeycloakStartupDiagnostics.MaximumCount);
        first.ElapsedMilliseconds.Should().Be(125);
        first.MilestoneMilliseconds.Take(3).Should().Equal(0, 125, -1);
        first.CachedProviderState.Should().Be(KeycloakCachedProviderState.NotCreated);
        first.ReadinessOutcome.Should().Be(KeycloakReadinessOutcome.NotAttempted);

        clock.Advance(TimeSpan.FromHours(1));
        diagnostics.Mark("provider_starting");
        var later = diagnostics.Snapshot(TestcontainersStates.Running);
        later.FixtureOrdinal.Should().Be(first.FixtureOrdinal, "the ordinal only correlates one local fixture");
        later.ElapsedMilliseconds.Should().Be(KeycloakStartupDiagnostics.MaximumMilliseconds);
        later.MilestoneMilliseconds[Array.IndexOf(KeycloakStartupDiagnostics.Milestones, "provider_starting")]
            .Should().Be(KeycloakStartupDiagnostics.MaximumMilliseconds);
        first.ElapsedMilliseconds.Should().Be(125);
        first.MilestoneMilliseconds[Array.IndexOf(KeycloakStartupDiagnostics.Milestones, "provider_starting")]
            .Should().Be(-1, "a captured failure snapshot cannot change during cleanup");
        later.ToSentence().Should().Contain("cachedProvider=running.").And.NotContain("private-stage-canary");
        diagnostics.Snapshot((TestcontainersStates)int.MaxValue).ToSentence().Should().Contain("cachedProvider=unknown.");
    }

    [Fact]
    public async Task Readiness_observer_preserves_request_token_response_and_every_status_family()
    {
        var diagnostics = new KeycloakStartupDiagnostics();
        var statuses = new[]
        {
            (HttpStatusCode.OK, KeycloakReadinessOutcome.HttpOk),
            (HttpStatusCode.NoContent, KeycloakReadinessOutcome.HttpOtherSuccess),
            (HttpStatusCode.Found, KeycloakReadinessOutcome.HttpRedirect),
            (HttpStatusCode.NotFound, KeycloakReadinessOutcome.HttpClientError),
            (HttpStatusCode.ServiceUnavailable, KeycloakReadinessOutcome.HttpServerError),
            ((HttpStatusCode)999, KeycloakReadinessOutcome.HttpOther),
        };
        var count = 0;
        using var cancellation = new CancellationTokenSource();
        foreach (var (status, expected) in statuses)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://private-uri-canary.invalid/private-port-path");
            using var response = new HttpResponseMessage(status)
            { Content = new StringContent("private-provider-body-canary") };
            using var invoker = new HttpMessageInvoker(diagnostics.Observe(new Handler((actual, token) =>
            {
                actual.Should().BeSameAs(request);
                token.Should().Be(cancellation.Token);
                return Task.FromResult(response);
            })));
            (await invoker.SendAsync(request, cancellation.Token)).Should().BeSameAs(response);
            var observation = diagnostics.Snapshot(TestcontainersStates.Running);
            observation.ReadinessAttempts.Should().Be(++count);
            observation.ReadinessCompleted.Should().Be(count);
            observation.ReadinessOutcome.Should().Be(expected);
            observation.ToSentence().Should().NotContain("private-uri-canary").And.NotContain("private-port-path")
                .And.NotContain("private-provider-body-canary");
        }
    }

    [Fact]
    public async Task Readiness_faults_keep_the_original_exception_and_export_only_closed_outcomes()
    {
        const string canary = "private-provider-error-canary";
        var faults = new (Exception Error, KeycloakReadinessOutcome Outcome)[]
        {
            (new OperationCanceledException(canary), KeycloakReadinessOutcome.Cancelled),
            (new HttpRequestException(HttpRequestError.SecureConnectionError, canary), KeycloakReadinessOutcome.Tls),
            (new HttpRequestException(HttpRequestError.ConnectionError, canary), KeycloakReadinessOutcome.Connect),
            (new HttpRequestException(HttpRequestError.NameResolutionError, canary), KeycloakReadinessOutcome.Dns),
            (new HttpRequestException(canary), KeycloakReadinessOutcome.Transport),
            (new AuthenticationException(canary), KeycloakReadinessOutcome.Tls),
            (new InvalidOperationException(canary), KeycloakReadinessOutcome.Other),
        };
        foreach (var (error, expected) in faults)
        {
            var diagnostics = new KeycloakStartupDiagnostics();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://private.invalid/");
            using var invoker = new HttpMessageInvoker(diagnostics.Observe(new Handler((_, _) =>
                Task.FromException<HttpResponseMessage>(error))));
            var observed = await Record.ExceptionAsync(async () => { await invoker.SendAsync(request, CancellationToken.None); });
            observed.Should().BeSameAs(error);
            var observation = diagnostics.Snapshot(TestcontainersStates.Running);
            observation.ReadinessAttempts.Should().Be(1);
            observation.ReadinessCompleted.Should().Be(1);
            observation.ReadinessOutcome.Should().Be(expected);
            observation.ToSentence().Should().NotContain(canary).And.NotContain("https:");
        }
    }

    [Fact]
    public async Task Inflight_snapshot_returns_without_waiting_and_cancellation_still_reaches_the_handler()
    {
        var diagnostics = new KeycloakStartupDiagnostics();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://private.invalid/");
        using var invoker = new HttpMessageInvoker(diagnostics.Observe(new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));
        var pending = invoker.SendAsync(request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var inflight = diagnostics.Snapshot(TestcontainersStates.Running);
        inflight.ReadinessAttempts.Should().Be(1);
        inflight.ReadinessCompleted.Should().Be(0);
        inflight.ReadinessOutcome.Should().Be(KeycloakReadinessOutcome.Pending);
        cancellation.Cancel();
        var act = async () => { await pending.WaitAsync(TimeSpan.FromSeconds(5)); };
        await act.Should().ThrowAsync<OperationCanceledException>();
        diagnostics.Snapshot(TestcontainersStates.Running).ReadinessOutcome.Should().Be(KeycloakReadinessOutcome.Cancelled);
        inflight.ReadinessCompleted.Should().Be(0, "the snapshot is immutable after capture");
    }

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

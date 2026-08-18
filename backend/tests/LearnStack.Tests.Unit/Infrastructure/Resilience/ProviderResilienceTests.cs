using FluentAssertions;
using LearnStack.Infrastructure.Resilience;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Resilience;
using Polly.Timeout;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Resilience;

/// <summary>
/// ProviderResilience contract per ADR-0032 § Sub-decision 5. Phase 02a
/// Packet 3 ships the socket; these tests assert the pipeline is buildable
/// with the canonical options shape, retries non-client provider failures,
/// and skips retry for client errors.
/// </summary>
public sealed class ProviderResilienceTests
{
    private interface ITestPort
    {
    }

    [Fact]
    public void Pipeline_Is_Built_With_Default_Options()
    {
        var sut = new ProviderResilience<ITestPort>("test", new ResilienceOptions());

        sut.PortName.Should().Be("test");
        sut.Pipeline.Should().NotBeNull();
    }

    [Fact]
    public async Task Retry_Activates_On_Server_Side_ProviderException()
    {
        var options = new ResilienceOptions
        {
            Retry = new RetryOptions { MaxRetryAttempts = 2, DelaySeconds = 0, UseJitter = false, Enabled = true },
            CircuitBreaker = new CircuitBreakerOptions { Enabled = false },
            Timeout = new TimeoutOptions { Enabled = false },
        };

        var sut = new ProviderResilience<ITestPort>("test", options);
        var attempts = 0;

        var act = async () => await sut.Pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            throw new ProviderException("test", "upstream 5xx", isClientError: false);
#pragma warning disable CS0162 // Unreachable code: makes the lambda's return type explicit.
            return ValueTask.CompletedTask;
#pragma warning restore CS0162
        });

        await act.Should().ThrowAsync<ProviderException>();
        attempts.Should().Be(3, "MaxRetryAttempts = 2 retries means 1 initial + 2 retries = 3 invocations");
    }

    [Fact]
    public async Task Retry_Does_Not_Trigger_On_Client_Side_ProviderException()
    {
        var options = new ResilienceOptions
        {
            Retry = new RetryOptions { MaxRetryAttempts = 5, DelaySeconds = 0, UseJitter = false, Enabled = true },
            CircuitBreaker = new CircuitBreakerOptions { Enabled = false },
            Timeout = new TimeoutOptions { Enabled = false },
        };

        var sut = new ProviderResilience<ITestPort>("test", options);
        var attempts = 0;

        var act = async () => await sut.Pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            throw new ProviderException("test", "bad input", isClientError: true);
#pragma warning disable CS0162
            return ValueTask.CompletedTask;
#pragma warning restore CS0162
        });

        await act.Should().ThrowAsync<ProviderException>();
        attempts.Should().Be(1, "client errors are not retried — Standards 09 § Retry vs Don't Retry");
    }

    [Fact]
    public async Task Retry_Activates_On_Pipeline_Timeout()
    {
        var options = new ResilienceOptions
        {
            Retry = new RetryOptions { MaxRetryAttempts = 2, DelaySeconds = 0, UseJitter = false, Enabled = true },
            CircuitBreaker = new CircuitBreakerOptions { Enabled = false },
            Timeout = new TimeoutOptions { Enabled = true, TotalSeconds = 0.05 },
        };

        var sut = new ProviderResilience<ITestPort>("test", options);
        var attempts = 0;

        var act = async () => await sut.Pipeline.ExecuteAsync(async ct =>
        {
            attempts++;
            // Observes the token the Timeout strategy cancels — mirrors how
            // a real provider call (e.g. HttpClient) respects the ambient
            // cancellation token instead of blocking past it.
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        });

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        attempts.Should().Be(3, "MaxRetryAttempts = 2 retries means 1 initial + 2 retries = 3 invocations, each timing out");
    }

    [Fact]
    public async Task Caller_Cancellation_Is_Not_Retried_As_A_Timeout()
    {
        // Same enabled retry as Retry_Activates_On_Pipeline_Timeout, but the
        // callback cancels the caller's own token mid-execution instead of
        // hitting the pipeline's Timeout strategy — retry's ShouldHandle
        // must tell the two apart and let real cancellation through.
        var options = new ResilienceOptions
        {
            Retry = new RetryOptions { MaxRetryAttempts = 2, DelaySeconds = 0, UseJitter = false, Enabled = true },
            CircuitBreaker = new CircuitBreakerOptions { Enabled = false },
            Timeout = new TimeoutOptions { Enabled = false },
        };

        var sut = new ProviderResilience<ITestPort>("test", options);
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var act = async () => await sut.Pipeline.ExecuteAsync(async (ct) =>
        {
            attempts++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1, "caller-initiated cancellation must propagate immediately, not be retried as a timeout");
    }
}

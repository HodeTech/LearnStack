using FluentAssertions;
using LearnStack.Infrastructure.Resilience;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Resilience;
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
            Retry = new RetryOptions { MaxAttempts = 2, DelaySeconds = 0, UseJitter = false, Enabled = true },
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
        attempts.Should().Be(3, "MaxAttempts = 2 retries means 1 initial + 2 retries = 3 invocations");
    }

    [Fact]
    public async Task Retry_Does_Not_Trigger_On_Client_Side_ProviderException()
    {
        var options = new ResilienceOptions
        {
            Retry = new RetryOptions { MaxAttempts = 5, DelaySeconds = 0, UseJitter = false, Enabled = true },
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
}

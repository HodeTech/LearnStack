using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Errors;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

/// <summary>
/// The Sentry / OpenTelemetry boundary, per
/// <see href="../../../../../docs/standards/09-error-handling.md">Standards 09
/// § Sentry vs OpenTelemetry — Error Capture Boundary</see> and
/// <see href="../../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
/// § Sub-decision 7</see>.
/// </summary>
/// <remarks>
/// The table is a binding rule with a single enforcement point —
/// <c>LearnStackExceptionHandler.ShouldCapture</c> — and until now no test at
/// all. Every row is one case here, because the cost of getting a row wrong is
/// asymmetric: a missing capture hides a bug, and a spurious one lets an
/// anonymous caller fill the error tracker one request at a time.
/// </remarks>
public sealed class ErrorCaptureBoundaryTests
{
    [Fact]
    public void An_Unhandled_Exception_Is_Captured()
    {
        // "Bug or infra; high-signal."
        LearnStackExceptionHandler.ShouldCapture(new InvalidOperationException())
            .Should().BeTrue();
    }

    [Fact]
    public void A_LearnStackException_Is_Captured()
    {
        // "Leaked from a failing layer."
        LearnStackExceptionHandler.ShouldCapture(new DomainException("invariant broken"))
            .Should().BeTrue();
    }

    [Fact]
    public void A_Provider_Failure_Is_Captured()
    {
        // "Upstream infra failure."
        LearnStackExceptionHandler.ShouldCapture(Provider(isClientError: false))
            .Should().BeTrue();
    }

    [Fact]
    public void A_Providers_Own_Client_Error_Is_Not_Captured()
    {
        // "Provider's user-error; not our bug."
        LearnStackExceptionHandler.ShouldCapture(Provider(isClientError: true))
            .Should().BeFalse();
    }

    [Fact]
    public void A_Client_Disconnect_Is_Not_Captured()
    {
        // "Noise; not actionable."
        LearnStackExceptionHandler.ShouldCapture(new OperationCanceledException())
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    public void A_Clients_Malformed_Request_Is_Not_Captured(int status)
    {
        // Kestrel — and RequestBodyLimit — throw this for a request the client
        // got wrong. Capturing it hands an anonymous caller a switch that fills
        // the error tracker and marks a span failed, which is the same failure
        // shape the correlation-header echo had before Packet 4 removed it.
        LearnStackExceptionHandler.ShouldCapture(new BadHttpRequestException("too big", status))
            .Should().BeFalse();
    }

    [Fact]
    public void A_5xx_BadHttpRequestException_Is_Still_Captured()
    {
        // The status decides, not the type. A 5xx in this shape is ours.
        LearnStackExceptionHandler.ShouldCapture(
                new BadHttpRequestException("server side", StatusCodes.Status500InternalServerError))
            .Should().BeTrue();
    }

    private static TestProviderException Provider(bool isClientError) =>
        new(isClientError);

    private sealed class TestProviderException(bool isClientError)
        : ProviderException("test-provider", "upstream said so", isClientError)
    {
    }
}

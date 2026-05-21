using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Errors;

/// <summary>
/// Wraps an upstream provider failure surfaced at the adapter boundary.
/// Per <see href="../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
/// § Sub-decision 5</see> every adapter under
/// <c>LearnStack.Infrastructure.&lt;Adapter&gt;</c> translates SDK exception
/// types into the appropriate <see cref="ProviderException"/> subclass; the
/// architecture test <c>Adapters_Wrap_Provider_Exceptions</c> enforces that
/// SDK exception types never leave the adapter assembly.
/// </summary>
/// <remarks>
/// The <see cref="IsClientError"/> flag splits the Sentry-capture boundary
/// (Standards 09 § Sentry vs OpenTelemetry — Error Capture Boundary):
/// <c>true</c> for 4xx upstream (provider's user-mistake, no Sentry capture),
/// <c>false</c> for 5xx upstream / timeouts (Sentry-captured infra failure).
/// </remarks>
public class ProviderException : LearnStackException
{
    private static readonly Error DefaultError = new(
        new LocalizedMessage("lockey_dependency_unavailable"));

    public ProviderException(
        string providerName,
        string message,
        bool isClientError,
        Exception? innerException = null)
        : base(DefaultError, message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ProviderName = providerName;
        IsClientError = isClientError;
    }

    public ProviderException(
        Error error,
        string providerName,
        string message,
        bool isClientError,
        Exception? innerException = null)
        : base(error, message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ProviderName = providerName;
        IsClientError = isClientError;
    }

    /// <summary>
    /// Stable provider identifier (e.g. <c>"livekit"</c>, <c>"stripe"</c>,
    /// <c>"meilisearch"</c>) tagged on metrics / spans / Sentry events. Must
    /// not leak to end users (Standards 09 § Provider Failures).
    /// </summary>
    public string ProviderName { get; }

    /// <summary>
    /// <c>true</c> when the upstream response is a 4xx-equivalent (the
    /// adapter caller passed bad input). The L1 handler skips Sentry capture
    /// for client errors. <c>false</c> when the upstream returned 5xx /
    /// timeout — captured to Sentry as an infra fault.
    /// </summary>
    public bool IsClientError { get; }
}

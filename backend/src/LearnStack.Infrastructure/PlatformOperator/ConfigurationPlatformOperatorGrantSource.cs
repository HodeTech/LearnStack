using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>
/// The shipped grant adapter: grants read from configuration, evaluated on every act
/// (ADR-0101 § 6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Evaluated per act, never cached beyond the options monitor.</b> A removed grant takes
/// effect on the next act once the configuration provider reloads. A provider that cannot
/// reload needs a restart, and the operator-access runbook says so.
/// </para>
/// <para>
/// <b>Fail closed.</b> A reload that fails validation throws from the monitor; the adapter
/// answers "not granted" for everyone and logs once per failure rather than keeping a stale
/// answer.
/// </para>
/// </remarks>
public sealed class ConfigurationPlatformOperatorGrantSource(
    IOptionsMonitor<PlatformOperatorGrantOptions> monitor,
    ILogger<ConfigurationPlatformOperatorGrantSource> logger) : IPlatformOperatorGrantSource
{
    private readonly IOptionsMonitor<PlatformOperatorGrantOptions> _monitor =
        monitor ?? throw new ArgumentNullException(nameof(monitor));

    private readonly ILogger<ConfigurationPlatformOperatorGrantSource> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public ValueTask<bool> IsGrantedAsync(
        string subjectDigest, string permissionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionKey);

        PlatformOperatorGrantOptions current;
        try
        {
            current = _monitor.CurrentValue;
        }
        catch (OptionsValidationException failure)
        {
            LogInvalidReload(_logger, failure);
            return ValueTask.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(current.Issuer))
        {
            return ValueTask.FromResult(false);
        }

        foreach (var grant in current.Grants)
        {
            if (string.IsNullOrWhiteSpace(grant.Subject)
                || !string.Equals(
                    PlatformOperatorSubject.Digest(current.Issuer, grant.Subject), subjectDigest, StringComparison.Ordinal))
            {
                continue;
            }

            return ValueTask.FromResult(
                grant.Permissions.Contains(permissionKey, StringComparer.Ordinal));
        }

        return ValueTask.FromResult(false);
    }

    private static readonly Action<ILogger, Exception?> LogInvalidReload =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(7101, nameof(LogInvalidReload)),
            "PlatformOperator grants failed validation on reload; every operator act is refused "
            + "until the configuration is valid.");
}

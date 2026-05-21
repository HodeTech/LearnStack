using Serilog.Core;
using Serilog.Events;

namespace LearnStack.Infrastructure.Observability.Serilog;

/// <summary>
/// Serilog enricher that scrubs sensitive log-scope properties before the
/// formatter touches them. Per
/// <see href="../../../../docs/standards/10-observability.md">Standards 10
/// § Logging Rules</see> and
/// <see href="../../../../docs/standards/11-security.md">Standards 11 § Sensitive Data Exposure</see>:
/// passwords, tokens, DSNs, JWTs, API keys, national identifiers,
/// authorization headers, full payment payloads must never reach the
/// console or the OTLP sink.
/// </summary>
/// <remarks>
/// <para>
/// The enricher rewrites matching properties in place to the constant
/// <see cref="RedactedValue"/>. A property is "sensitive" when its name
/// contains one of the case-insensitive tokens listed in
/// <see cref="SensitiveKeyTokens"/>. The list is conservative; production
/// deployments can layer additional patterns by composing a richer
/// enricher.
/// </para>
/// <para>
/// Stack traces and exception messages are NOT redacted — they ride
/// through <see cref="LogEvent.Exception"/>, which the enricher leaves
/// alone. Modules must follow Standards 11 (never put secrets in
/// exception messages) so the boundary is honest.
/// </para>
/// </remarks>
public sealed class RedactSensitiveFieldsEnricher : ILogEventEnricher
{
    public const string RedactedValue = "***REDACTED***";

    private static readonly string[] SensitiveKeyTokens =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "apikey",
        "api_key",
        "authorization",
        "auth_header",
        "dsn",
        "jwt",
        "credential",
        "ssn",
        "tckn",
        "iban",
        "cardnumber",
        "card_number",
        "cvv",
        "cvc",
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var propertyName in logEvent.Properties.Keys.ToArray())
        {
            if (IsSensitive(propertyName))
            {
                logEvent.AddOrUpdateProperty(
                    propertyFactory.CreateProperty(propertyName, RedactedValue));
            }
        }
    }

    private static bool IsSensitive(string propertyName)
    {
        foreach (var token in SensitiveKeyTokens)
        {
            if (propertyName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

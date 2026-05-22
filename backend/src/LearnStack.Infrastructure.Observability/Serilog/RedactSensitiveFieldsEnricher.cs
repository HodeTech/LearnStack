using LearnStack.SharedKernel.Secrets;
using Serilog.Core;
using Serilog.Events;

namespace LearnStack.Infrastructure.Observability.Serilog;

/// <summary>
/// Serilog enricher that scrubs sensitive log-scope properties before the
/// formatter touches them. Per
/// <see href="../../../../docs/standards/10-observability.md">Standards 10
/// § Logging Rules</see> and
/// <see href="../../../../docs/standards/11-security.md">Standards 11 § Sensitive Data Exposure</see>:
/// passwords, tokens, DSNs, JWTs, API keys, national / corporate identifiers,
/// authorization headers, full payment payloads must never reach the
/// console or the OTLP sink.
/// </summary>
/// <remarks>
/// <para>
/// The token list lives in
/// <see cref="LearnStack.SharedKernel.Secrets.SensitiveTokenCatalog"/> —
/// the canonical source the air-gapped <c>LocalFileErrorTracker</c> shares
/// so the two redaction surfaces cannot drift. Adding a token there lights
/// up both paths together.
/// </para>
/// <para>
/// Stack traces and exception messages are NOT redacted — they ride
/// through <see cref="LogEvent.Exception"/>, which the enricher leaves
/// alone. Modules must follow Standards 11 (never put secrets in
/// exception messages) so the boundary stays honest.
/// </para>
/// <para>
/// TODO(2026-05-21, @platform): augment the Serilog pipeline with a Roslyn
/// analyzer (extending <c>LearnStack.Analyzers</c>) in Phase 02b or later
/// that flags string-interpolated <c>throw new ...Exception($"...{token}...")</c>
/// patterns in <c>Domain</c> + <c>Application</c> projects. Today the
/// "no secrets in exception messages" rule rests on Standards 11 review
/// discipline; promoting it to a compile-time check closes the last
/// gap the runtime redactor cannot.
/// </para>
/// </remarks>
public sealed class RedactSensitiveFieldsEnricher : ILogEventEnricher
{
    /// <summary>Substituted in place of any matched property value.</summary>
    public const string RedactedValue = SensitiveTokenCatalog.RedactedValue;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        // Two-pass to avoid a steady-state per-event allocation: the common
        // case (no sensitive properties) allocates nothing. The `sensitive`
        // list materialises only when the first match is found, so we never
        // ToArray the key set up front. The collect-then-mutate split is
        // also what keeps us from mutating logEvent.Properties while
        // enumerating it.
        List<string>? sensitive = null;
        foreach (var propertyName in logEvent.Properties.Keys)
        {
            if (SensitiveTokenCatalog.IsSensitive(propertyName))
            {
                (sensitive ??= []).Add(propertyName);
            }
        }

        if (sensitive is null)
        {
            return;
        }

        foreach (var propertyName in sensitive)
        {
            logEvent.AddOrUpdateProperty(
                propertyFactory.CreateProperty(propertyName, RedactedValue));
        }
    }
}

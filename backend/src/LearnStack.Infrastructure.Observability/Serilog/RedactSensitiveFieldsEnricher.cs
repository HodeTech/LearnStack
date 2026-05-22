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
/// Redaction is <strong>recursive</strong>: destructured objects
/// (<c>{@User}</c>), dictionaries, and sequences are walked so a sensitive
/// field nested inside a non-sensitive top-level property
/// (e.g. <c>User.Password</c>) is scrubbed too. Reconstruction is lazy —
/// a value with no sensitive descendant is returned by reference, so the
/// common (clean) event allocates nothing.
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

    private static readonly ScalarValue RedactedScalar = new(RedactedValue);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        // Collect first, mutate after — never mutate logEvent.Properties
        // while enumerating it. The list materialises only when a top-level
        // property needs rewriting (its name is sensitive, or a sensitive
        // value lives nested inside it), so a clean event allocates nothing.
        List<LogEventProperty>? rewrites = null;

        foreach (var (name, value) in logEvent.Properties)
        {
            if (SensitiveTokenCatalog.IsSensitive(name))
            {
                (rewrites ??= []).Add(new LogEventProperty(name, RedactedScalar));
                continue;
            }

            var redacted = Redact(value);
            if (!ReferenceEquals(redacted, value))
            {
                (rewrites ??= []).Add(new LogEventProperty(name, redacted));
            }
        }

        if (rewrites is null)
        {
            return;
        }

        foreach (var property in rewrites)
        {
            logEvent.AddOrUpdateProperty(property);
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="value"/> with every sensitively-named
    /// nested property / dictionary key redacted, or the same instance when
    /// nothing changed (so clean values cost no allocation).
    /// </summary>
    private static LogEventPropertyValue Redact(LogEventPropertyValue value)
    {
        switch (value)
        {
            case StructureValue structure:
            {
                List<LogEventProperty>? newProps = null;
                for (var i = 0; i < structure.Properties.Count; i++)
                {
                    var prop = structure.Properties[i];
                    var newValue = SensitiveTokenCatalog.IsSensitive(prop.Name)
                        ? RedactedScalar
                        : Redact(prop.Value);

                    if (newProps is null && ReferenceEquals(newValue, prop.Value))
                    {
                        continue;
                    }

                    newProps ??= [.. structure.Properties.Take(i)];
                    newProps.Add(new LogEventProperty(prop.Name, newValue));
                }

                return newProps is null
                    ? structure
                    : new StructureValue(newProps, structure.TypeTag);
            }

            case DictionaryValue dictionary:
            {
                // DictionaryValue.Elements is keyed by ScalarValue (not an
                // indexable list). On the first change, copy the whole map
                // then overwrite the changed keys; a clean dictionary returns
                // by reference.
                Dictionary<ScalarValue, LogEventPropertyValue>? newElements = null;
                foreach (var element in dictionary.Elements)
                {
                    var keyName = element.Key.Value?.ToString();
                    var newValue = keyName is not null && SensitiveTokenCatalog.IsSensitive(keyName)
                        ? RedactedScalar
                        : Redact(element.Value);

                    if (ReferenceEquals(newValue, element.Value))
                    {
                        continue;
                    }

                    newElements ??= new Dictionary<ScalarValue, LogEventPropertyValue>(dictionary.Elements);
                    newElements[element.Key] = newValue;
                }

                return newElements is null
                    ? dictionary
                    : new DictionaryValue(newElements);
            }

            case SequenceValue sequence:
            {
                List<LogEventPropertyValue>? newItems = null;
                for (var i = 0; i < sequence.Elements.Count; i++)
                {
                    var item = sequence.Elements[i];
                    var newItem = Redact(item);

                    if (newItems is null && ReferenceEquals(newItem, item))
                    {
                        continue;
                    }

                    newItems ??= [.. sequence.Elements.Take(i)];
                    newItems.Add(newItem);
                }

                return newItems is null ? sequence : new SequenceValue(newItems);
            }

            default:
                return value; // ScalarValue and unknown value kinds pass through.
        }
    }
}

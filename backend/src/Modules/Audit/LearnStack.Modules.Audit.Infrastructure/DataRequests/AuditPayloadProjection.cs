using System.Text.Json;
using System.Text.RegularExpressions;

namespace LearnStack.Modules.Audit.Infrastructure.DataRequests;

/// <summary>
/// Rewrites a captured audit payload for a subject's export: every identifier becomes <c>self</c> when it is the
/// subject's own and <c>third_party</c> otherwise
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 3</see>,
/// <see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 5</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every identifier.</b> An export excludes internal identifiers, the subject's <c>UserId</c> included, and
/// replaces another person's reference with <c>third_party</c>. Without the locators of ADR-0108 § 7 (P03-10
/// Step 2) a captured payload does not say which identifier names a person, so the projection treats each one as
/// a possible person reference: the subject's own become <c>self</c>, everything else <c>third_party</c>. It
/// discloses less than a locator-aware projection would, never more. Identifiers embedded in a longer string, the
/// instance-qualified <c>changes</c> paths included, are rewritten the same way, in values and in property names.
/// </para>
/// <para>
/// <b>Property names stay distinct.</b> Rewriting names can make two of one object's names equal (two
/// Memberships keyed by identifier both become <c>third_party</c>), and a JSON object with a repeated name is
/// read differently by different parsers. Within one object the first occurrence of a rewritten name keeps it;
/// each later one takes the first free of <c>name_2</c>, <c>name_3</c>, … in document order, so the output is
/// deterministic and every name in it is unique. The suffix counts names, never identifies a person.
/// </para>
/// <para>
/// Values the capture already replaced with the redaction sentinel stay replaced; nothing else is changed.
/// </para>
/// </remarks>
public static partial class AuditPayloadProjection
{
    public const string Self = "self";
    public const string ThirdParty = "third_party";

    /// <summary>Writes <paramref name="rawJson"/> rewritten, or JSON <c>null</c> when there is no payload.</summary>
    public static void Write(Utf8JsonWriter json, string? rawJson, IReadOnlySet<Guid> own)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(own);
        if (rawJson is null)
        {
            json.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(rawJson);
        Write(json, document.RootElement, own);
    }

    /// <summary>Rewrites every identifier embedded in <paramref name="value"/>.</summary>
    public static string Rewrite(string value, IReadOnlySet<Guid> own) =>
        Identifier().Replace(value, match => own.Contains(Guid.Parse(match.ValueSpan)) ? Self : ThirdParty);

    private static void Write(Utf8JsonWriter json, JsonElement element, IReadOnlySet<Guid> own)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                json.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    json.WritePropertyName(Distinct(Rewrite(property.Name, own), names));
                    Write(json, property.Value, own);
                }

                json.WriteEndObject();
                break;
            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(json, item, own);
                }

                json.WriteEndArray();
                break;
            case JsonValueKind.String:
                json.WriteStringValue(Rewrite(element.GetString()!, own));
                break;
            default:
                element.WriteTo(json);
                break;
        }
    }

    /// <summary>The name itself when the object has not used it, else the first free <c>name_n</c> from 2.</summary>
    private static string Distinct(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = name + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    [GeneratedRegex("[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Identifier();
}

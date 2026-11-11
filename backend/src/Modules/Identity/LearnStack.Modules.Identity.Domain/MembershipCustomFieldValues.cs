using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Modules.Identity.Domain;

public sealed record MembershipCustomFieldValue(string ConceptKey, Guid DefinitionId, int SchemaVersion,
    [property: PiiSensitive] JsonElement Envelope, Guid? ConsentEvidenceId)
{
    public override string ToString() => nameof(MembershipCustomFieldValue);
}

/// <summary>Contained immutable pins; no schema, classification or current-consent inference belongs to storage.</summary>
public static class MembershipCustomFieldValues
{
    public const int MaximumSlots = 20;
    public const int MaximumCollectionBytes = 32 * 1024;
    public const int MaximumConceptKeyLength = 100;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<MembershipCustomFieldValue> Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaximumSlots
                || document.RootElement.EnumerateArray().Any(slot => slot.ValueKind != JsonValueKind.Object
                    || slot.EnumerateObject().Count() != 5 || !slot.TryGetProperty("conceptKey", out _)
                    || !slot.TryGetProperty("definitionId", out _) || !slot.TryGetProperty("schemaVersion", out _)
                    || !slot.TryGetProperty("envelope", out _) || !slot.TryGetProperty("consentEvidenceId", out _)))
                throw new InvalidOperationException("Membership custom-field storage is invalid.");
            var values = JsonSerializer.Deserialize<MembershipCustomFieldValue[]>(json, Options);
            if (values is null || values.Length > MaximumSlots || values.Any(value => value is null))
                throw new InvalidOperationException("Membership custom-field storage is invalid.");
            // jsonb expands exponents and reorders object properties. Reconstruct
            // the admitted binary64 representation before applying logical budgets.
            var normalized = values.Select(value => value with { Envelope = NormalizeEnvelope(value.Envelope) }).ToArray();
            if (Serialize(normalized) is null) throw new InvalidOperationException("Membership custom-field storage is invalid.");
            return normalized;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Membership custom-field storage is invalid.");
        }
    }

    public static string? Serialize(IReadOnlyCollection<MembershipCustomFieldValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaximumSlots || values.Any(value => value is null || !ValidKey(value.ConceptKey)
            || value.DefinitionId == Guid.Empty || value.DefinitionId.Version != 7 || value.SchemaVersion < 1
            || value.ConsentEvidenceId is { } evidence && (evidence == Guid.Empty || evidence.Version != 7)
            || !ValidEnvelope(value.Envelope))
            || values.Select(value => value.ConceptKey).Distinct(StringComparer.Ordinal).Count() != values.Count) return null;
        var json = JsonSerializer.Serialize(values.OrderBy(value => value.ConceptKey, StringComparer.Ordinal), Options);
        return Encoding.UTF8.GetByteCount(json) <= MaximumCollectionBytes ? json : null;
    }

    private static JsonElement NormalizeEnvelope(JsonElement envelope)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(envelope, writer);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();

        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) Write(item, writer);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.Number:
                    if (!element.TryGetDouble(out var number) || !double.IsFinite(number))
                        throw new InvalidOperationException("Membership custom-field storage is invalid.");
                    writer.WriteNumberValue(number);
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }

    public static bool ValidKey(string? key) => key is { Length: > 0 and <= MaximumConceptKeyLength }
        && key is not ("constructor" or "prototype" or "__proto__")
        && LearnStack.SharedKernel.Domain.UrlSlug.IsUrlSafe(key);

    public static bool ValidEnvelope(JsonElement envelope) => envelope.ValueKind == JsonValueKind.Object
        && envelope.EnumerateObject().Count() == 1 && envelope.TryGetProperty("value", out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        && LearnStack.SharedKernel.Domain.JsonValue.IsWellFormed(envelope.GetRawText());
}

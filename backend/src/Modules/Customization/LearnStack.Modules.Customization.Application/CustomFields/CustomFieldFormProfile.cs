using JsonValue = LearnStack.SharedKernel.Domain.JsonValue;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Validation;

namespace LearnStack.Modules.Customization.Application.CustomFields;

public sealed record AdmittedCustomFieldSchema(string Schema, string Witness, int MaximumValueBytes)
{
    public override string ToString() => nameof(AdmittedCustomFieldSchema);
}

/// <summary>The additional finite form grammar; general schema admission remains authoritative.</summary>
public sealed class CustomFieldFormProfile(IJsonSchemaValidator schemas) : ICustomFieldValueValidator
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
    /// <summary>The logical authoring body embeds a schema object; internal command wrappers are not wire payload.</summary>
    public static bool FitsAuthoringInput(CustomFieldDefinitionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.JsonSchema) || Encoding.UTF8.GetByteCount(input.JsonSchema) > SchemaByteLimit) return false;
        try
        {
            using var schema = JsonDocument.Parse(input.JsonSchema, new JsonDocumentOptions { MaxDepth = 8 });
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                input.TargetEntity,
                input.Key,
                input.SchemaVersion,
                Schema = schema.RootElement,
                input.Labels,
                PiiCategory = input.PiiCategory.ToString(),
                input.IsRequired,
                input.Sort,
                input.PurposeKey,
                input.Notices,
                input.FormLabels,
            }, WireOptions).Length <= InputByteLimit;
        }
        catch (JsonException) { return false; }
    }

    public Result<string> NormalizeStored(string jsonSchema)
    {
        var admitted = RevalidateStored(jsonSchema);
        return admitted.IsFailure ? Result<string>.Fail(admitted.Error) : Result.Ok(admitted.Value.Schema);
    }

    public const int SchemaByteLimit = 2048;
    public const int InputByteLimit = 4096;
    public const long SafeInteger = 9007199254740991;
    public const int ActiveLimit = 20;
    public const int ConceptLimit = 100;
    public const int RevisionLimit = 1000;
    public const int DraftLimit = 20;

    public Result<AdmittedCustomFieldSchema> Admit(string schema) => AdmitCore(schema, storedRepresentation: false);

    // jsonb adds whitespace and expands exponent notation. Revalidate its semantics
    // and the normalized byte budget without treating its physical text as author input.
    internal Result<AdmittedCustomFieldSchema> RevalidateStored(string schema) => AdmitCore(schema, storedRepresentation: true);

    private Result<AdmittedCustomFieldSchema> AdmitCore(string schema, bool storedRepresentation)
    {
        if (string.IsNullOrWhiteSpace(schema)
            || !storedRepresentation && Encoding.UTF8.GetByteCount(schema) > SchemaByteLimit) return Refuse<AdmittedCustomFieldSchema>();
        // Suppress the general validator's tenant-authored pointer names at this boundary.
        if (schemas.AdmitSchema(schema).IsFailure) return Refuse<AdmittedCustomFieldSchema>();
        try
        {
            using var doc = JsonDocument.Parse(schema);
            var root = doc.RootElement;
            if (!Exact(root, "$schema", "type", "properties", "required", "additionalProperties")
                || root.GetProperty("$schema").GetString() != "https://json-schema.org/draft/2020-12/schema"
                || root.GetProperty("type").GetString() != "object"
                || root.GetProperty("additionalProperties").ValueKind != JsonValueKind.False
                || !Exact(root.GetProperty("properties"), "value")
                || !Required(root.GetProperty("required"), ["value"], requireAll: true)) return Refuse<AdmittedCustomFieldSchema>();
            var node = Build(root.GetProperty("properties").GetProperty("value"), 0);
            if (node is null) return Refuse<AdmittedCustomFieldSchema>();
            var envelope = new JsonObject
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["type"] = "object",
                ["properties"] = new JsonObject { ["value"] = node.Schema },
                ["required"] = new JsonArray("value"),
                ["additionalProperties"] = false
            };
            var canonical = envelope.ToJsonString();
            var witness = new JsonObject { ["value"] = node.Witness }.ToJsonString();
            if (Encoding.UTF8.GetByteCount(canonical) > SchemaByteLimit
                || schemas.AdmitSchema(canonical).IsFailure || schemas.ValidateInstance(canonical, witness).IsFailure)
                return Refuse<AdmittedCustomFieldSchema>();
            return Result.Ok(new AdmittedCustomFieldSchema(canonical, witness, node.Bytes));
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { return Refuse<AdmittedCustomFieldSchema>(); }
    }

    public Result<string> ValidateAndNormalize(CustomFieldDefinitionDto definition, string envelopeJson)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(envelopeJson) || Encoding.UTF8.GetByteCount(envelopeJson) > InputByteLimit) return Refuse<string>();
        try
        {
            using var input = JsonDocument.Parse(envelopeJson);
            using var schema = JsonDocument.Parse(definition.JsonSchema);
            if (!Exact(input.RootElement, "value")) return Refuse<string>();
            var value = NormalizeValue(schema.RootElement.GetProperty("properties").GetProperty("value"), input.RootElement.GetProperty("value"));
            if (value is null) return Refuse<string>();
            var result = new JsonObject { ["value"] = value }.ToJsonString();
            if (Encoding.UTF8.GetByteCount(result) > InputByteLimit || schemas.ValidateInstance(definition.JsonSchema, result).IsFailure)
                return Refuse<string>();
            return Result.Ok(result);
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { return Refuse<string>(); }
    }

    /// <summary>Includes full per-operation/pin structure and worst-case percent encoding.</summary>
    public static bool FitsSubmission(IEnumerable<(string Key, int MaximumValueBytes)> fields)
    {
        long bytes = 256; // operation, collection, expected Profile version and request delimiters
        foreach (var (key, maximum) in fields)
        {
            if (maximum < 1 || !ValidKey(key)) return false;
            // Complete slot: concept, revision UUID/version, consent UUID, envelope and separators.
            bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(key)) + maximum + 256L;
            if (bytes > InputByteLimit) return false;
        }
        return bytes <= InputByteLimit && 3 * bytes + 1024 <= 16384;
    }

    public static bool ValidKey(string? value) => LearnStack.Modules.Customization.Domain.CustomFieldKey.IsValid(value);

    /// <summary>Reparse in canonical ordinal property order, independent of jsonb's object ordering.</summary>
    public static IReadOnlyList<CustomFieldControlShape> DescribeControls(string admittedSchema)
    {
        using var document = JsonDocument.Parse(admittedSchema);
        var result = new List<CustomFieldControlShape>();
        Visit(document.RootElement.GetProperty("properties").GetProperty("value"), "/");
        return result.AsReadOnly();
        void Visit(JsonElement node, string location)
        {
            var type = node.GetProperty("type").GetString()!;
            result.Add(new(location, type, node.TryGetProperty("enum", out var choices) ? choices.GetArrayLength() : 0));
            if (type == "object")
            {
                var index = 0;
                foreach (var property in node.GetProperty("properties").EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    Visit(property.Value, location + (location == "/" ? "" : "/") + (index++).ToString(CultureInfo.InvariantCulture));
            }
            else if (type == "array") Visit(node.GetProperty("items"), location + (location == "/" ? "" : "/") + "0");
        }
    }

    public static bool ValidControlLabels(string schema, IReadOnlyDictionary<string, string> rootLabels,
        IReadOnlyList<CustomFieldControlLabels>? labels, string defaultLocale, IReadOnlyList<string> enabledLocales)
    {
        if (!ValidLocalizedLabels(rootLabels, defaultLocale, enabledLocales, 128)) return false;
        var shapes = DescribeControls(schema).Where(shape => shape.Location != "/" || shape.EnumChoices > 0).ToArray();
        if (labels is null) return shapes.Length == 0;
        if (labels.Count != shapes.Length || labels.Any(label => label is null)
            || labels.Select(label => label.Location).Distinct(StringComparer.Ordinal).Count() != labels.Count) return false;
        foreach (var label in labels)
        {
            var shape = shapes.SingleOrDefault(shape => shape.Location == label.Location);
            if (shape is null || (shape.Location == "/" ? label.Labels is not null
                    : !ValidLocalizedLabels(label.Labels, defaultLocale, enabledLocales, 128))
                || (shape.EnumChoices == 0 ? label.Choices is not null : label.Choices is null || label.Choices.Count != shape.EnumChoices)
                || label.Choices is not null && label.Choices.Any(choice => !ValidLocalizedLabels(choice, defaultLocale, enabledLocales, 128))) return false;
        }
        return true;
    }

    public static bool ValidLocalizedLabels(IReadOnlyDictionary<string, string>? labels, string defaultLocale,
        IReadOnlyList<string> enabledLocales, int maximumBytes) => labels is { Count: > 0 and <= 50 }
        && labels.Keys.Distinct(StringComparer.Ordinal).Count() == labels.Count && labels.ContainsKey(defaultLocale)
        && labels.All(pair => enabledLocales.Contains(pair.Key, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(pair.Value)
            && JsonValue.IsStorableText(pair.Value) && Encoding.UTF8.GetByteCount(pair.Value) <= maximumBytes);

    public static string? SerializeControlLabels(IReadOnlyList<CustomFieldControlLabels>? labels) => labels is null ? null
        : JsonSerializer.Serialize(labels.OrderBy(label => label.Location, StringComparer.Ordinal).Select(label => new CustomFieldControlLabels(
            label.Location, label.Labels is null ? null : new SortedDictionary<string, string>(label.Labels.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal),
            label.Choices?.Select(choice => (IReadOnlyDictionary<string, string>)new SortedDictionary<string, string>(
                choice.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal)).ToArray())).ToArray());

    private static Node? Build(JsonElement schema, int depth)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return null;
        var kind = type.GetString();
        if (kind == "string")
        {
            if (!Allowed(schema, ["type", "minLength", "maxLength"], ["enum"])
                || !Integer(schema, "minLength", 1, 256, out var min) || !Integer(schema, "maxLength", min, 256, out var max)) return null;
            string witness = new('a', min);
            if (schema.TryGetProperty("enum", out var choices))
            {
                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() is < 1 or > 20) return null;
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.String) return null;
                    var text = choice.GetString()!;
                    var length = text.EnumerateRunes().Count();
                    if (!JsonValue.IsStorableText(text) || length < min || length > max || !unique.Add(text)) return null;
                }
                witness = choices[0].GetString()!;
                // Enumerations have an exact finite serialization bound.
                return new(JsonNode.Parse(schema.GetRawText())!, JsonValueNode(witness), unique.Max(x => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(x))));
            }
            return new(JsonNode.Parse(schema.GetRawText())!, JsonValueNode(witness), 2 + 12 * max);
        }
        if (kind is "integer" or "number")
        {
            if (!Exact(schema, "type", "minimum", "maximum") || !Numeric(schema.GetProperty("minimum"), kind == "integer", out var min)
                || !Numeric(schema.GetProperty("maximum"), kind == "integer", out var max) || min > max) return null;
            return new(new JsonObject { ["type"] = kind, ["minimum"] = min, ["maximum"] = max }, JsonValueNode(min), 32);
        }
        if (kind == "boolean") return Exact(schema, "type") ? new(JsonNode.Parse(schema.GetRawText())!, JsonValueNode(false), 5) : null;
        if (kind == "object")
        {
            if (++depth > 2 || !Exact(schema, "type", "properties", "required", "additionalProperties")
                || schema.GetProperty("additionalProperties").ValueKind != JsonValueKind.False) return null;
            var properties = schema.GetProperty("properties");
            if (properties.ValueKind != JsonValueKind.Object) return null;
            var children = properties.EnumerateObject().ToArray();
            if (children.Length is < 1 or > 10 || children.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != children.Length
                || children.Any(x => !ValidKey(x.Name)) || !Required(schema.GetProperty("required"), children.Select(x => x.Name).ToArray(), false)) return null;
            var shapes = new JsonObject(); var witness = new JsonObject(); var bytes = 2;
            // All optional children are proved nonempty as well; all appear in the witness.
            foreach (var child in children.OrderBy(child => child.Name, StringComparer.Ordinal))
            {
                var built = Build(child.Value, depth); if (built is null) return null;
                shapes.Add(child.Name, built.Schema); witness.Add(child.Name, built.Witness);
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(child.Name)) + 2 + built.Bytes);
            }
            return new(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = shapes,
                ["required"] = JsonNode.Parse(schema.GetProperty("required").GetRawText()),
                ["additionalProperties"] = false
            }, witness, bytes);
        }
        if (kind == "array")
        {
            if (++depth > 2 || !Exact(schema, "type", "items", "minItems", "maxItems")
                || !Integer(schema, "minItems", 0, 10, out var min) || !Integer(schema, "maxItems", Math.Max(1, min), 10, out var max)) return null;
            var child = Build(schema.GetProperty("items"), depth); if (child is null) return null;
            var witness = new JsonArray(); for (var i = 0; i < min; i++) witness.Add(child.Witness.DeepClone());
            return new(new JsonObject { ["type"] = "array", ["items"] = child.Schema, ["minItems"] = min, ["maxItems"] = max }, witness,
                checked(2 + max * (child.Bytes + 1)));
        }
        return null;
    }

    private static JsonNode? NormalizeValue(JsonElement schema, JsonElement value)
    {
        switch (schema.GetProperty("type").GetString())
        {
            case "integer":
            case "number":
                return Numeric(value, schema.GetProperty("type").GetString() == "integer", out var number) ? JsonValueNode(number) : null;
            case "string": return value.ValueKind == JsonValueKind.String && JsonValue.IsStorableText(value.GetString()!) ? JsonValueNode(value.GetString()!) : null;
            case "boolean": return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValueNode(value.GetBoolean()) : null;
            case "object":
                if (value.ValueKind != JsonValueKind.Object) return null;
                var result = new JsonObject(); var properties = schema.GetProperty("properties");
                foreach (var child in value.EnumerateObject())
                {
                    if (!ValidKey(child.Name) || result.ContainsKey(child.Name) || !properties.TryGetProperty(child.Name, out var shape)) return null;
                    var normalized = NormalizeValue(shape, child.Value); if (normalized is null) return null;
                    result.Add(child.Name, normalized);
                }
                return result;
            case "array":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 10) return null;
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    var normalized = NormalizeValue(schema.GetProperty("items"), item); if (normalized is null) return null;
                    array.Add(normalized);
                }
                return array;
            default: return null;
        }
    }

    private static bool Numeric(JsonElement token, bool integer, out double value)
    {
        value = 0;
        if (token.ValueKind != JsonValueKind.Number || !token.TryGetDouble(out value) || !double.IsFinite(value)) return false;
        var raw = token.GetRawText();
        var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        var digits = new string(mantissa.Where(char.IsAsciiDigit).ToArray());
        if (digits.All(character => character == '0')) { value = 0; return true; }
        if (value == 0 || double.IsInteger(value) && Math.Abs(value) > SafeInteger) return false;
        if (exponentAt >= 0 && !int.TryParse(raw.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out _)) return false;
        var exponent = exponentAt < 0 ? 0 : int.Parse(raw.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var decimalAt = mantissa.IndexOf('.');
        var shift = (long)exponent - (decimalAt < 0 ? 0 : mantissa.Length - decimalAt - 1);
        var significant = digits.TrimStart('0');
        var trailing = significant.Length - significant.TrimEnd('0').Length;
        var isIntegralSource = shift >= 0 || -shift <= trailing;
        if (!isIntegralSource) return !integer;
        // Determine integrality from the original decimal lexeme, without a decimal parser's rounding.
        if (significant.Length + shift > 16 || significant.Length + shift < 1) return false;
        var integralDigits = shift >= 0 ? significant + new string('0', (int)shift) : significant[..(significant.Length + (int)shift)];
        var exact = BigInteger.Parse(integralDigits, CultureInfo.InvariantCulture);
        if (raw[0] == '-') exact = -exact;
        return BigInteger.Abs(exact) <= SafeInteger && double.IsInteger(value) && exact == new BigInteger(value);
    }
    private static bool Integer(JsonElement schema, string property, int min, int max, out int value)
    { value = 0; return schema.TryGetProperty(property, out var token) && token.TryGetInt32(out value) && value >= min && value <= max; }
    private static bool Required(JsonElement value, string[] names, bool requireAll)
    {
        if (value.ValueKind != JsonValueKind.Array) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String || !names.Contains(item.GetString()!, StringComparer.Ordinal) || !found.Add(item.GetString()!)) return false;
        return !requireAll || found.Count == names.Length;
    }
    private static bool Exact(JsonElement value, params string[] keys) => Allowed(value, keys, []);
    private static bool Allowed(JsonElement value, string[] mandatory, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var keys = value.EnumerateObject().Select(x => x.Name).ToArray();
        return keys.Distinct(StringComparer.Ordinal).Count() == keys.Length && mandatory.All(keys.Contains)
            && keys.All(key => mandatory.Contains(key, StringComparer.Ordinal) || optional.Contains(key, StringComparer.Ordinal));
    }
    private static System.Text.Json.Nodes.JsonValue JsonValueNode<T>(T value) => System.Text.Json.Nodes.JsonValue.Create(value)!;
    private static Result<T> Refuse<T>() => Result<T>.Fail(new Error(new LocalizedMessage("lockey_validation_failed"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal) { ["/"] = [new("lockey_invalid_value")] }));
    private sealed record Node(JsonNode Schema, JsonNode Witness, int Bytes);
}

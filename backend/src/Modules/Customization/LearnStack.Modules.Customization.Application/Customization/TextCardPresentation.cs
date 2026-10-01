using System.Collections.Immutable;
using System.Text.Json;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>ADR-0051 semantic resolution, after schema admission; no registry or locale lookup.</summary>
public static class TextCardPresentation
{
    private const int MaxFailures = 25;

    public static Result<ImmutableArray<TextCardFieldDto>> Resolve(string admittedSchema, string rendererKey)
    {
        ArgumentNullException.ThrowIfNull(admittedSchema);
        using var document = JsonDocument.Parse(admittedSchema);
        var root = document.RootElement;
        if (!root.TryGetProperty("x-fields", out var descriptors))
        {
            return Result.Ok(ImmutableArray<TextCardFieldDto>.Empty);
        }

        var failures = new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal);
        void Refuse(string location)
        {
            if (failures.Count < MaxFailures)
            {
                failures.TryAdd(location, [new LocalizedMessage("lockey_schema_extension_unresolved")]);
            }
        }

        if (rendererKey != "default-card")
        {
            Refuse("/x-fields");
        }

        CheckShape(root, "", "object", Refuse);
        if (!root.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False)
        {
            Refuse("/additionalProperties");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("properties").EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                Refuse("/properties/" + Escape(property.Name));
            }

            CheckShape(property.Value, "/properties/" + Escape(property.Name), "string", Refuse);
        }

        var fields = ImmutableArray.CreateBuilder<TextCardFieldDto>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        if (descriptors.ValueKind != JsonValueKind.Array || descriptors.GetArrayLength() == 0)
        {
            Refuse("/x-fields");
        }
        else
        {
            var index = 0;
            foreach (var descriptor in descriptors.EnumerateArray())
            {
                var location = "/x-fields/" + index++;
                if (descriptor.ValueKind != JsonValueKind.Object)
                {
                    Refuse(location);
                    continue;
                }

                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in descriptor.EnumerateObject())
                {
                    if (!members.Add(member.Name) || member.Name is not ("name" or "label"))
                    {
                        Refuse(location + "/" + Escape(member.Name));
                    }
                }

                if (!descriptor.TryGetProperty("name", out var nameValue)
                    || nameValue.ValueKind != JsonValueKind.String)
                {
                    Refuse(location + "/name");
                    continue;
                }

                var name = nameValue.GetString()!;
                if (!names.Contains(name) || !covered.Add(name))
                {
                    Refuse(location + "/name");
                }

                if (!descriptor.TryGetProperty("label", out var label))
                {
                    Refuse(location + "/label");
                    continue;
                }

                try
                {
                    fields.Add(new TextCardFieldDto(name, LocalizedText.FromJson(label.GetRawText())));
                }
                catch (ArgumentException)
                {
                    Refuse(location + "/label");
                }
            }
        }

        foreach (var missing in names.Except(covered))
        {
            Refuse("/properties/" + Escape(missing));
        }

        return failures.Count == 0
            ? Result.Ok(fields.ToImmutable())
            : Result<ImmutableArray<TextCardFieldDto>>.Fail(
                new Error(new LocalizedMessage("lockey_validation_failed"), failures));
    }

    private static void CheckShape(JsonElement schema, string location, string expectedType, Action<string> refuse)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            refuse(location);
            return;
        }

        if (!schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String || type.GetString() != expectedType)
        {
            refuse(location + "/type");
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in schema.EnumerateObject())
        {
            if (!members.Add(property.Name))
            {
                refuse(location + "/" + Escape(property.Name));
            }

            // Unknown inert annotations stay legal. Every shape-changing applicator,
            // alternate value set, field format and rendering extension is refused.
            if (property.Name is "$ref" or "$dynamicRef" or "allOf" or "anyOf" or "oneOf"
                or "not" or "if" or "then" or "else" or "enum" or "const"
                or "items" or "prefixItems" or "contains" or "unevaluatedItems"
                or "dependentSchemas" or "patternProperties" or "unevaluatedProperties"
                || (expectedType == "string" && (property.Name is "format" or "properties"
                    or "additionalProperties" || property.Name.StartsWith("x-", StringComparison.Ordinal))))
            {
                refuse(location + "/" + Escape(property.Name));
            }
        }
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}

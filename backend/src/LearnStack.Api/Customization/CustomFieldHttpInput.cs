using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.SharedKernel.DataProtection;
namespace LearnStack.Api.Customization;
/// <summary>Closed bounded wire parsing before a serializer can discard duplicate names or coerce numbers.</summary>
internal static class CustomFieldHttpInput
{
    internal static async Task<JsonDocument?> ReadAsync(HttpContext http, bool allowQuery, params string[] fields)
    {
        if (!allowQuery && http.Request.Query.Count != 0 || !AdministrationHttpInput.SupportsJsonMedia(http.Request)) return null;
        var bytes = await AdministrationHttpInput.ReadBodyBytesAsync(http);
        if (bytes.Length > 4096) return null;
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            if (AdministrationHttpInput.HasFields(doc.RootElement, fields) && Unique(doc.RootElement)) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        catch (DecoderFallbackException) { }
        catch (InvalidOperationException) { }
        return null;
    }
    private static bool Unique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String) return LearnStack.SharedKernel.Domain.JsonValue.IsStorableText(node.GetString()!);
        if (node.ValueKind == JsonValueKind.Array) return node.EnumerateArray().All(Unique);
        if (node.ValueKind != JsonValueKind.Object) return true;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return node.EnumerateObject().All(p => LearnStack.SharedKernel.Domain.JsonValue.IsStorableText(p.Name) && keys.Add(p.Name) && p.Name is not ("__proto__" or "prototype" or "constructor") && Unique(p.Value));
    }
    internal static bool TryDefinition(JsonElement body, out CustomFieldDefinitionInput? input)
    {
        input = null;
        if (!AdministrationHttpInput.TryText(body, "targetEntity", false, 16, out var target) || target != "Membership"
            || !AdministrationHttpInput.TryText(body, "key", false, 100, out var key)
            || !Int(body, "schemaVersion", out var schemaVersion) || schemaVersion < 1
            || !Int(body, "sort", out var sort) || sort is < 0 or > short.MaxValue
            || !Bool(body, "isRequired", out var required)
            || !AdministrationHttpInput.TryText(body, "piiCategory", false, 16, out var category)
            || category is not ("None" or "Identity" or "Behaviour" or "Sensitive")
            || !TryMap(body.GetProperty("labels"), false, out var labels)
            || !TryMap(body.GetProperty("notices"), true, out var notices)
            || !AdministrationHttpInput.TryText(body, "purposeKey", true, 64, out var purpose)
            || body.GetProperty("schema").ValueKind != JsonValueKind.Object
            || Encoding.UTF8.GetByteCount(body.GetProperty("schema").GetRawText()) > 2048) return false;
        IReadOnlyList<CustomFieldControlLabels>? controls = null;
        var list = body.GetProperty("formLabels");
        if (list.ValueKind != JsonValueKind.Null)
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 111) return false;
            var collected = new List<CustomFieldControlLabels>();
            foreach (var item in list.EnumerateArray())
            {
                if (!AdministrationHttpInput.HasFields(item, "location", "labels", "choices")
                    || !AdministrationHttpInput.TryText(item, "location", false, 64, out var location)
                    || !TryMap(item.GetProperty("labels"), true, out var text)) return false;
                IReadOnlyList<IReadOnlyDictionary<string, string>>? choices = null;
                var choiceNode = item.GetProperty("choices");
                if (choiceNode.ValueKind != JsonValueKind.Null)
                {
                    if (choiceNode.ValueKind != JsonValueKind.Array || choiceNode.GetArrayLength() > 20) return false;
                    var found = new List<IReadOnlyDictionary<string, string>>();
                    foreach (var choice in choiceNode.EnumerateArray())
                    { if (!TryMap(choice, false, out var value)) return false; found.Add(value!); }
                    choices = found;
                }
                collected.Add(new(location!, text, choices));
            }
            controls = collected;
        }
        input = new(target!, key!, schemaVersion, body.GetProperty("schema").GetRawText(), labels!,
            Enum.Parse<PersonalDataCategory>(category!), required, (short)sort, purpose, notices, controls);
        return true;
    }
    private static bool TryMap(JsonElement node, bool nullable, out IReadOnlyDictionary<string, string>? result)
    {
        result = null;
        if (node.ValueKind == JsonValueKind.Null) return nullable;
        if (node.ValueKind != JsonValueKind.Object) return false;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in node.EnumerateObject())
        { if (map.Count >= 50 || p.Value.ValueKind != JsonValueKind.String || !map.TryAdd(p.Name, p.Value.GetString()!)) return false; }
        result = map; return true;
    }
    internal static bool Bool(JsonElement body, string name, out bool value)
    { value = false; var node = body.GetProperty(name); if (node.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; value = node.GetBoolean(); return true; }
    internal static bool Int(JsonElement body, string name, out int value)
    { value = 0; var n = body.GetProperty(name); return n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out value); }
    internal static bool Sequence(JsonElement body, string name, out long value)
    { value = 0; var n = body.GetProperty(name); return n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out value) && value is >= 0 and <= 9007199254740991; }
    internal static bool Id(JsonElement body, string name, bool nullable, out Guid? value)
    {
        value = null; var n = body.GetProperty(name); if (n.ValueKind == JsonValueKind.Null) return nullable;
        if (n.ValueKind != JsonValueKind.String || !AdministrationHttpInput.TryId(n.GetString(), out var id)) return false;
        value = id; return true;
    }
    internal static bool Patch(JsonElement body, out MembershipCustomFieldPatch? patch)
    {
        patch = null; var sets = body.GetProperty("set"); var removes = body.GetProperty("remove");
        if (sets.ValueKind != JsonValueKind.Array || removes.ValueKind != JsonValueKind.Array || sets.GetArrayLength() > 20 || removes.GetArrayLength() > 20) return false;
        var sb = ImmutableArray.CreateBuilder<MembershipCustomFieldSet>(); var rb = ImmutableArray.CreateBuilder<MembershipCustomFieldRemove>();
        foreach (var item in sets.EnumerateArray())
        {
            if (!AdministrationHttpInput.HasFields(item, "conceptKey", "definitionId", "schemaVersion", "envelope", "consentEvidenceId")
                || !AdministrationHttpInput.TryText(item, "conceptKey", false, 100, out var key) || !Id(item, "definitionId", false, out var id)
                || !Int(item, "schemaVersion", out var revision) || revision < 1 || !Id(item, "consentEvidenceId", true, out var evidence)
                || !AdministrationHttpInput.HasFields(item.GetProperty("envelope"), "value")) return false;
            sb.Add(new(key!, id!.Value, revision, item.GetProperty("envelope").Clone(), evidence));
        }
        foreach (var item in removes.EnumerateArray())
        {
            if (!AdministrationHttpInput.HasFields(item, "conceptKey", "definitionId", "schemaVersion")
                || !AdministrationHttpInput.TryText(item, "conceptKey", false, 100, out var key) || !Id(item, "definitionId", false, out var id)
                || !Int(item, "schemaVersion", out var revision) || revision < 1) return false;
            rb.Add(new(key!, id!.Value, revision));
        }
        patch = new(sb.ToImmutable(), rb.ToImmutable()); return true;
    }
    internal static readonly string[] DefinitionFields = ["targetEntity", "key", "schemaVersion", "schema", "labels", "piiCategory", "isRequired", "sort", "purposeKey", "notices", "formLabels"];
}

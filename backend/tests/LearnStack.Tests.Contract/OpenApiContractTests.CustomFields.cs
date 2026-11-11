using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Contract;

public sealed partial class OpenApiContractTests
{
    // Maintainer-approved HTTP census, independent of production descriptors and transformers.
    private sealed record CustomFieldContract(string Path, string Method, string Id, string Response,
        string? Input = null, bool Cas = false, bool Creates = false, bool RetainedSource = false, bool VersionedRead = false);
    private static readonly CustomFieldContract[] CustomFieldOperations =
    [
        new("/api/v1/customization/custom-field-definitions", "get", "ListCustomFieldDefinitions", "CustomFieldDefinitionPageResponse"),
        new("/api/v1/customization/custom-field-definitions/{id}", "get", "GetCustomFieldDefinition", "CustomFieldDefinitionResponse", VersionedRead: true),
        new("/api/v1/customization/custom-field-definitions/{id}/revisions", "get", "ListCustomFieldDefinitionRevisions", "CustomFieldDefinitionPageResponse"),
        new("/api/v1/customization/custom-field-definitions", "post", "RegisterCustomFieldDefinition", "CustomFieldReceiptResponse", "CustomFieldDefinitionInput", Creates: true),
        new("/api/v1/customization/custom-field-definitions/{id}", "put", "ReviseCustomFieldDefinitionDraft", "CustomFieldReceiptResponse", "CustomFieldDefinitionInput", Cas: true),
        new("/api/v1/customization/custom-field-definitions/{id}/publication", "post", "PublishCustomFieldDefinition", "CustomFieldReceiptResponse", "CustomFieldPublicationInput", Cas: true),
        new("/api/v1/customization/custom-field-definitions/{id}/retirement", "post", "RetireCustomFieldDefinition", "CustomFieldReceiptResponse", "EmptyAdministrationInput", Cas: true),
        new("/api/v1/customization/custom-field-definitions/{id}/discard", "post", "DiscardCustomFieldDefinitionDraft", "CustomFieldReceiptResponse", "EmptyAdministrationInput", Cas: true),
        new("/api/v1/identity/memberships/{id}/custom-fields", "get", "GetMembershipCustomFields", "MembershipCustomFieldsResponse", VersionedRead: true),
        new("/api/v1/identity/me/custom-fields", "get", "GetOwnMembershipCustomFields", "MembershipCustomFieldsResponse", VersionedRead: true),
        new("/api/v1/identity/memberships/{id}/custom-fields", "patch", "SetMembershipCustomFields", "CustomFieldReceiptResponse", "MembershipCustomFieldPatch", Cas: true),
        new("/api/v1/identity/me/custom-fields", "patch", "SetOwnMembershipCustomFields", "CustomFieldReceiptResponse", "MembershipCustomFieldPatch", Cas: true),
        new("/api/v1/tenancy/membership-field-consents/own", "get", "GetOwnMembershipFieldConsent", "OwnFieldConsentCurrentResponse"),
        new("/api/v1/tenancy/membership-field-consents/own/scopes", "get", "ListOwnFieldConsentScopes", "ConsentScopePageResponse", RetainedSource: true),
        new("/api/v1/tenancy/membership-field-consents/own/history", "get", "ListOwnMembershipFieldConsents", "ConsentHistoryPageResponse", RetainedSource: true),
        new("/api/v1/tenancy/membership-field-consents/grants", "post", "GrantOwnMembershipFieldConsent", "FieldConsentReceiptResponse", "FieldConsentGrantInput"),
        new("/api/v1/tenancy/membership-field-consents/withdrawals", "post", "WithdrawOwnMembershipFieldConsent", "FieldConsentReceiptResponse", "FieldConsentWithdrawalInput", RetainedSource: true),
    ];
    private static readonly string[] CustomFieldSchemaNames =
    [
        "CustomFieldLocalizedText", "CustomFieldNoticeText", "CustomFieldNoticeHashes", "CustomFieldFormLabel", "CustomFieldSchemaNode0", "CustomFieldSchemaNode1", "CustomFieldSchemaNode2",
        "CustomFieldValueNode0", "CustomFieldValueNode1", "CustomFieldValueNode2", "CustomFieldFiniteSchema", "CustomFieldDefinitionInput",
        "CustomFieldDefinitionResponse", "CustomFieldReceiptResponse", "FieldConsentReceiptResponse", "CustomFieldDefinitionPageResponse",
        "CustomFieldPublicationInput", "CustomFieldValueEnvelope", "MembershipCustomFieldPatch", "MembershipCustomFieldsResponse",
        "FieldConsentGrantInput", "FieldConsentWithdrawalInput", "ConsentDetailsResponse", "OwnFieldConsentCurrentResponse",
        "ConsentScopePageResponse", "ConsentHistoryPageResponse",
    ];
    private static readonly Dictionary<string, string[]> CustomFieldObjects = new(StringComparer.Ordinal)
    {
        ["CustomFieldFormLabel"] = ["location", "labels", "choices"],
        ["CustomFieldFiniteSchema"] = ["$schema", "type", "properties", "required", "additionalProperties"],
        ["CustomFieldDefinitionInput"] = ["targetEntity", "key", "schemaVersion", "schema", "labels", "piiCategory", "isRequired", "sort", "purposeKey", "notices", "formLabels"],
        ["CustomFieldDefinitionResponse"] = ["targetEntity", "key", "schemaVersion", "schema", "labels", "piiCategory", "isRequired", "sort", "purposeKey", "notices", "formLabels", "definitionId", "status", "version", "isDiscarded", "maximumValueBytes", "activeRevision"],
        ["CustomFieldReceiptResponse"] = ["id", "version"],
        ["FieldConsentReceiptResponse"] = ["evidenceId", "sequence", "isGranted"],
        ["CustomFieldDefinitionPageResponse"] = ["items", "pageInfo"],
        ["CustomFieldPublicationInput"] = ["expectedIncumbentId", "expectedIncumbentVersion"],
        ["CustomFieldValueEnvelope"] = ["value"],
        ["MembershipCustomFieldPatch"] = ["set", "remove"],
        ["MembershipCustomFieldsResponse"] = ["membershipId", "version", "fields"],
        ["FieldConsentGrantInput"] = ["definitionId", "definitionVersion", "noticeLocale", "expectedNoticeHash", "expectedSequence", "expectedEvidenceId", "confirmed"],
        ["FieldConsentWithdrawalInput"] = ["evidenceId", "expectedSequence", "expectedEvidenceId", "confirmed"],
        ["ConsentDetailsResponse"] = ["evidenceId", "sequence", "isGranted", "membershipId", "definitionId", "definitionVersion", "purposeKey", "noticeLocale", "noticeHash", "noticeText", "labels"],
        ["OwnFieldConsentCurrentResponse"] = ["consent"],
        ["ConsentScopePageResponse"] = ["items", "pageInfo"],
        ["ConsentHistoryPageResponse"] = ["items", "pageInfo"],
    };

    [Fact]
    public async Task Custom_field_contract_controls_reject_wrong_operation_credential_method_cas_and_receipt_boundaries()
    {
        var clean = await Served();
        CustomFieldOperations.Should().HaveCount(17);
        CustomFieldOperations.Count(value => value.Input is not null).Should().Be(9);
        CustomFieldOperations.Count(value => value.RetainedSource).Should().Be(3);
        CustomFieldContractErrors(clean).Should().BeEmpty();
        CustomFieldSchemaErrors(clean).Should().BeEmpty();
        foreach (var contract in CustomFieldOperations)
        {
            CfMutant(clean, "custom-field-operations", dirty => dirty["paths"]![contract.Path]!["head"] = dirty["paths"]![contract.Path]![contract.Method]!.DeepClone());
            CfMutant(clean, "custom-field-security", dirty => dirty["paths"]![contract.Path]![contract.Method]!["security"]![0]!.AsObject().Remove("TenantBearer"));
            if (contract.RetainedSource)
                CfMutant(clean, "custom-field-security", dirty => dirty["paths"]![contract.Path]![contract.Method]!["security"]![0]!.AsObject().Remove("NativeSessionSource"));
            else
                CfMutant(clean, "custom-field-security", dirty => dirty["paths"]![contract.Path]![contract.Method]!["security"]![0]!["NativeSessionSource"] = new JsonArray());
            if (contract.Input is not null)
                CfMutant(clean, "custom-field-parameters", dirty => dirty["paths"]![contract.Path]![contract.Method]!["parameters"]!.AsArray().Add(new JsonObject { ["name"] = "arbitraryUrl", ["in"] = "query", ["schema"] = AdminText() }));
            else
                CfMutant(clean, "custom-field-body", dirty => dirty["paths"]![contract.Path]![contract.Method]!["requestBody"] = new JsonObject { ["required"] = true });
        }
        foreach (var contract in CustomFieldOperations.Where(value => value.Input is not null))
        {
            CfMutant(clean, "custom-field-parameters", dirty =>
            {
                var parameters = dirty["paths"]![contract.Path]![contract.Method]!["parameters"]!.AsArray();
                parameters.Remove(parameters.Single(value => value!["name"]!.GetValue<string>() == "Idempotency-Key"));
            });
            if (contract.Cas)
                CfMutant(clean, "custom-field-parameters", dirty =>
                {
                    var parameters = dirty["paths"]![contract.Path]![contract.Method]!["parameters"]!.AsArray();
                    parameters.Remove(parameters.Single(value => value!["name"]!.GetValue<string>() == "If-Match"));
                });
            else if (!contract.Creates)
            {
                CfMutant(clean, "custom-field-parameters", dirty => dirty["paths"]![contract.Path]![contract.Method]!["parameters"]!.AsArray().Add(new JsonObject { ["name"] = "If-Match", ["in"] = "header", ["required"] = true, ["schema"] = AdminEntityTag() }));
                CfMutant(clean, "custom-field-headers", dirty => dirty["paths"]![contract.Path]![contract.Method]!["responses"]!["200"]!["headers"]!["ETag"] = new JsonObject { ["required"] = true, ["schema"] = AdminEntityTag() });
            }
        }
        foreach (var receipt in new[] { "CustomFieldReceiptResponse", "FieldConsentReceiptResponse" })
            foreach (var leak in new[] { "schema", "notices", "envelope", "noticeText", "labels", "membershipId" })
                CfMutant(clean, "custom-field-schema:" + receipt, dirty => dirty["components"]!["schemas"]![receipt]!["properties"]![leak] = AdminText());
    }

    [Fact]
    public async Task Custom_field_schema_controls_reject_unbounded_inputs_retired_privacy_loss_and_third_containers()
    {
        var clean = await Served();
        foreach (var (name, _) in CustomFieldObjects)
            CfMutant(clean, "custom-field-schema:" + name, dirty => dirty["components"]!["schemas"]![name]!["additionalProperties"] = true);
        foreach (var (name, field, maximum) in new[] { ("MembershipCustomFieldPatch", "set", 21), ("MembershipCustomFieldPatch", "remove", 21), ("MembershipCustomFieldsResponse", "fields", 41), ("CustomFieldDefinitionPageResponse", "items", 21), ("ConsentHistoryPageResponse", "items", 21), ("ConsentScopePageResponse", "items", 21) })
            CfMutant(clean, "custom-field-bounds", dirty => dirty["components"]!["schemas"]![name]!["properties"]![field]!["maxItems"] = maximum);
        CfMutant(clean, "custom-field-finite-depth", dirty => dirty["components"]!["schemas"]!["CustomFieldSchemaNode2"]!["oneOf"]!.AsArray().Add(AdminObject(("type", AdminEnum("object")), ("properties", AdminObject()))));
        CfMutant(clean, "custom-field-finite-depth", dirty => dirty["components"]!["schemas"]!["CustomFieldValueNode2"]!["anyOf"]!.AsArray().Add(new JsonObject { ["type"] = "array", ["items"] = AdminText() }));
        CfMutant(clean, "custom-field-envelope", dirty => dirty["components"]!["schemas"]!["CustomFieldFiniteSchema"]!["properties"]!["properties"] = AdminText());
        CfMutant(clean, "custom-field-predecessor", dirty => dirty["components"]!["schemas"]!["FieldConsentWithdrawalInput"]!["properties"]!["expectedSequence"]!["minimum"] = 0);
        CfMutant(clean, "custom-field-predecessor", dirty => dirty["components"]!["schemas"]!["FieldConsentGrantInput"]!["properties"]!["expectedSequence"]!["type"] = "string");
        CfMutant(clean, "custom-field-membership", dirty => dirty["components"]!["schemas"]!["MembershipCustomFieldsResponse"]!["properties"]!["fields"]!["items"]!["properties"]!["definition"]!["properties"]!.AsObject().Remove("status"));
        CfMutant(clean, "custom-field-version", dirty => dirty["components"]!["schemas"]!["CustomFieldReceiptResponse"]!["properties"]!["version"] = new JsonObject { ["type"] = "integer" });
    }

    private static List<string> CustomFieldContractErrors(JsonNode document)
    {
        var errors = new List<string>();
        foreach (var group in CustomFieldOperations.GroupBy(value => value.Path, StringComparer.Ordinal))
        {
            if (document["paths"]?[group.Key] is not JsonObject path || !path.Select(value => value.Key).ToHashSet(StringComparer.Ordinal).SetEquals(group.Select(value => value.Method)))
                errors.Add("custom-field-operations");
            foreach (var contract in group)
            {
                if (document["paths"]?[contract.Path]?[contract.Method] is not JsonObject operation)
                { errors.Add("custom-field-operations"); continue; }
                if (operation["operationId"]?.GetValue<string>() != contract.Id || operation["x-version-introduced"]?.GetValue<string>() != "v1") errors.Add("custom-field-operations");
                if (operation["security"] is not JsonArray { Count: 1 } security || security[0] is not JsonObject requirement
                    || requirement.Count != (contract.RetainedSource ? 2 : 1) || requirement["TenantBearer"] is not JsonArray { Count: 0 }
                    || (contract.RetainedSource && requirement["NativeSessionSource"] is not JsonArray { Count: 0 })) errors.Add("custom-field-security");
                var expected = new Dictionary<string, (string Location, bool Required)>(StringComparer.Ordinal);
                if (contract.Path.Contains("{id}", StringComparison.Ordinal)) expected["id"] = ("path", true);
                if (contract.Id is "ListCustomFieldDefinitions" or "ListCustomFieldDefinitionRevisions") expected["cursor"] = ("query", false);
                if (contract.Id == "ListCustomFieldDefinitions") expected["status"] = ("query", false);
                if (contract.Id is "ListOwnFieldConsentScopes" or "ListOwnMembershipFieldConsents") { expected["cursor"] = ("query", false); expected["limit"] = ("query", false); }
                if (contract.Id is "ListOwnMembershipFieldConsents" or "WithdrawOwnMembershipFieldConsent" or "GetOwnMembershipFieldConsent") expected["membershipId"] = ("query", true);
                if (contract.Id == "GetOwnMembershipFieldConsent") { expected["definitionId"] = ("query", true); expected["definitionVersion"] = ("query", true); }
                if (contract.Input is not null)
                {
                    expected["Idempotency-Key"] = ("header", true);
                    if (contract.Cas) expected["If-Match"] = ("header", true);
                    if (operation["requestBody"]?["required"]?.GetValue<bool>() != true || !AdminMediaMatches(operation["requestBody"]?["content"], "application/json", contract.Input)) errors.Add("custom-field-body");
                    var description = operation["description"]?.GetValue<string>() ?? string.Empty;
                    if (!description.Contains("4 KiB", StringComparison.Ordinal) || !description.Contains("HMAC", StringComparison.Ordinal) || !description.Contains("duplicate", StringComparison.Ordinal)) errors.Add("custom-field-body");
                }
                else if (operation.ContainsKey("requestBody")) errors.Add("custom-field-body");
                var parameters = operation["parameters"] as JsonArray ?? [];
                if (parameters.Count != expected.Count || !parameters.Select(value => value?["name"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys)) errors.Add("custom-field-parameters");
                foreach (var parameter in parameters)
                {
                    if (parameter?["name"]?.GetValue<string>() is not { } name || !expected.TryGetValue(name, out var descriptor)
                        || parameter["in"]?.GetValue<string>() != descriptor.Location || (parameter["required"]?.GetValue<bool>() ?? false) != descriptor.Required)
                    { errors.Add("custom-field-parameters"); continue; }
                    if (name == "Idempotency-Key" && !AdminSchemaMatches(parameter["schema"], new JsonObject { ["type"] = "string", ["minLength"] = 8, ["maxLength"] = 128, ["pattern"] = "^[!-~]{8,128}$" })) errors.Add("custom-field-parameters");
                    if (name == "limit" && (!CfBound(parameter["schema"], "minimum", "1") || !CfBound(parameter["schema"], "maximum", "20"))) errors.Add("custom-field-parameters");
                    if (name == "If-Match" && parameter["schema"]?["pattern"]?.GetValue<string>() != "^\"(0|[1-9][0-9]{0,18})\"$") errors.Add("custom-field-parameters");
                }
                if (operation["responses"] is not JsonObject responses) { errors.Add("custom-field-responses"); continue; }
                var success = contract.Creates ? "201" : "200";
                if (!responses.Select(value => value.Key).ToHashSet(StringComparer.Ordinal).SetEquals(AdministrationErrors.Append(success).Concat(contract.Input is null ? [] : AdministrationConflicts))) errors.Add("custom-field-responses");
                foreach (var (status, response) in responses)
                {
                    if (!AdminLiteralHeader(response?["headers"]?["Cache-Control"], "no-store", status != "413")) errors.Add("custom-field-headers");
                    if (status != success) { if (!AdminMediaMatches(response?["content"], "application/problem+json", "ProblemDetails")) errors.Add("custom-field-responses"); continue; }
                    if (!AdminMediaMatches(response?["content"], "application/json", contract.Response)) errors.Add("custom-field-success");
                    var headers = response?["headers"] as JsonObject;
                    var names = new List<string> { "Cache-Control" };
                    if (contract.VersionedRead || contract.Cas || contract.Creates) { names.Add("ETag"); if (headers?["ETag"]?["required"]?.GetValue<bool>() != true || headers["ETag"]?["schema"]?["type"]?.GetValue<string>() != "string") errors.Add("custom-field-headers"); }
                    if (contract.Input is not null) { names.Add("Idempotency-Replayed"); if (!AdminLiteralHeader(headers?["Idempotency-Replayed"], "true", false)) errors.Add("custom-field-headers"); }
                    if (contract.Creates) { names.Add("Location"); if (headers?["Location"]?["required"]?.GetValue<bool>() != true) errors.Add("custom-field-headers"); }
                    if (headers is null || !headers.Select(value => value.Key).ToHashSet(StringComparer.Ordinal).SetEquals(names)) errors.Add("custom-field-headers");
                }
            }
        }
        errors.AddRange(CustomFieldSchemaErrors(document));
        return errors;
    }

    private static List<string> CustomFieldSchemaErrors(JsonNode document)
    {
        var errors = new List<string>(); var schemas = document["components"]?["schemas"];
        foreach (var (name, fields) in CustomFieldObjects)
            if (!CfObject(schemas?[name], fields)) errors.Add("custom-field-schema:" + name);
        foreach (var (name, field, maximum) in new[] { ("MembershipCustomFieldPatch", "set", "20"), ("MembershipCustomFieldPatch", "remove", "20"), ("MembershipCustomFieldsResponse", "fields", "40"), ("CustomFieldDefinitionPageResponse", "items", "20"), ("ConsentHistoryPageResponse", "items", "20"), ("ConsentScopePageResponse", "items", "20") })
            if (!CfBound(schemas?[name]?["properties"]?[field], "maxItems", maximum)) errors.Add("custom-field-bounds");
        foreach (var name in new[] { "CustomFieldDefinitionPageResponse", "ConsentHistoryPageResponse", "ConsentScopePageResponse" })
            if (!JsonNode.DeepEquals(schemas?[name]?["properties"]?["pageInfo"], AdminRef("PageInfo"))) errors.Add("custom-field-bounds");
        foreach (var (name, field) in new[] { ("CustomFieldReceiptResponse", "version"), ("FieldConsentReceiptResponse", "sequence"), ("MembershipCustomFieldsResponse", "version"), ("ConsentDetailsResponse", "sequence") })
        {
            var shape = schemas?[name]?["properties"]?[field];
            if (shape?["type"]?.GetValue<string>() != "string" || shape["pattern"]?.GetValue<string>() != "^(0|[1-9][0-9]{0,18})$" || !CfBound(shape, "maxLength", "19")) errors.Add("custom-field-version");
        }
        var patch = schemas?["MembershipCustomFieldPatch"]?["properties"];
        if (!CfObject(patch?["set"]?["items"], ["conceptKey", "definitionId", "schemaVersion", "envelope", "consentEvidenceId"]) || !CfObject(patch?["remove"]?["items"], ["conceptKey", "definitionId", "schemaVersion"])) errors.Add("custom-field-pins");
        var fieldProjection = schemas?["MembershipCustomFieldsResponse"]?["properties"]?["fields"]?["items"];
        if (!CfObject(fieldProjection, ["conceptKey", "definitionId", "schemaVersion", "state", "isRequired", "envelope", "definition", "consent"])
            || !CfObject(fieldProjection?["properties"]?["definition"], ["schema", "labels", "piiCategory", "sort", "purposeKey", "notices", "formLabels", "noticeHashes", "status"])) errors.Add("custom-field-membership");
        var envelope = schemas?["CustomFieldFiniteSchema"]?["properties"];
        if (!CfObject(envelope?["properties"], ["value"]) || !JsonNode.DeepEquals(envelope?["properties"]?["properties"]?["value"], AdminRef("CustomFieldSchemaNode0"))
            || !JsonNode.DeepEquals(schemas?["CustomFieldValueEnvelope"]?["properties"]?["value"], AdminRef("CustomFieldValueNode0"))) errors.Add("custom-field-envelope");
        for (var depth = 0; depth < 3; ++depth)
        {
            var branches = schemas?["CustomFieldSchemaNode" + depth]?["oneOf"] as JsonArray;
            var values = schemas?["CustomFieldValueNode" + depth]?["anyOf"] as JsonArray;
            if (branches?.Count != (depth == 2 ? 5 : 7) || values?.Count != (depth == 2 ? 3 : 5)) { errors.Add("custom-field-finite-depth"); continue; }
            foreach (var branch in branches!)
            {
                var type = branch?["properties"]?["type"]?["enum"]?[0]?.GetValue<string>();
                var names = type switch
                {
                    "string" => branch?["properties"]?["enum"] is null ? new[] { "type", "minLength", "maxLength" } : ["type", "minLength", "maxLength", "enum"],
                    "integer" or "number" => ["type", "minimum", "maximum"],
                    "boolean" => ["type"],
                    "object" => ["type", "properties", "required", "additionalProperties"],
                    "array" => ["type", "items", "minItems", "maxItems"],
                    _ => Array.Empty<string>(),
                };
                if (names.Length == 0 || !CfObject(branch, names) || (depth == 2 && type is "object" or "array")) errors.Add("custom-field-finite-depth");
                if (type == "string" && (!CfBound(branch?["properties"]?["maxLength"], "maximum", "256") || !CfBound(branch?["properties"]?["minLength"], "minimum", "1"))) errors.Add("custom-field-bounds");
                if (type == "object" && !CfBound(branch?["properties"]?["properties"], "maxProperties", "10")) errors.Add("custom-field-bounds");
                if (type == "array" && (!CfBound(branch?["properties"]?["maxItems"], "maximum", "10") || !CfBound(branch?["properties"]?["maxItems"], "minimum", "1"))) errors.Add("custom-field-bounds");
            }
            if (depth == 2 && values!.Any(value => value?["type"]?.GetValue<string>() is "object" or "array")) errors.Add("custom-field-finite-depth");
        }
        foreach (var name in new[] { "CustomFieldDefinitionInput", "CustomFieldDefinitionResponse" })
        {
            var properties = schemas?[name]?["properties"];
            if (!CfEnum(properties?["targetEntity"], ["Membership"]) || !CfEnum(properties?["piiCategory"], ["None", "Identity", "Behaviour", "Sensitive"])
                || !CfBound(properties?["schemaVersion"], "minimum", "1") || !CfBound(properties?["schemaVersion"], "maximum", "2147483647")
                || !CfBound(properties?["sort"], "minimum", "0") || !CfBound(properties?["sort"], "maximum", "32767")) errors.Add("custom-field-definition");
        }
        if (!CfNullable(schemas?["FieldConsentGrantInput"]?["properties"]?["expectedEvidenceId"])
            || CfNullable(schemas?["FieldConsentWithdrawalInput"]?["properties"]?["expectedEvidenceId"])) errors.Add("custom-field-predecessor");
        foreach (var (name, minimum) in new[] { ("FieldConsentGrantInput", "0"), ("FieldConsentWithdrawalInput", "1") })
        {
            var predecessor = schemas?[name]?["properties"]?["expectedSequence"];
            if (predecessor?["type"]?.GetValue<string>() != "integer" || !CfBound(predecessor, "minimum", minimum) || !CfBound(predecessor, "maximum", "9007199254740991")) errors.Add("custom-field-predecessor");
        }
        return errors;
    }
    private static bool CfObject(JsonNode? schema, string[] names) => schema?["type"]?.GetValue<string>() == "object"
        && schema["additionalProperties"]?.GetValue<bool>() == false && schema["properties"] is JsonObject properties && properties.Count == names.Length
        && properties.Select(value => value.Key).ToHashSet(StringComparer.Ordinal).SetEquals(names) && schema["required"] is JsonArray required
        && required.Count == names.Length && required.Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(names);
    private static bool CfEnum(JsonNode? schema, string[] values) => schema?["type"]?.GetValue<string>() == "string" && schema["enum"] is JsonArray items && items.Count == values.Length && items.Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(values);
    private static bool CfNullable(JsonNode? schema) => schema?["anyOf"] is JsonArray { Count: 2 } branches && branches.Count(value => value?["type"]?.GetValue<string>() == "null") == 1;
    private static bool CfBound(JsonNode? schema, string name, string expected) => schema?[name]?.ToJsonString() == expected;
    private static void CfMutant(JsonNode clean, string expected, Action<JsonNode> mutation)
    { var dirty = clean.DeepClone(); mutation(dirty); CustomFieldContractErrors(dirty).Should().Contain(expected); }
}

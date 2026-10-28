using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Contract;

public sealed partial class OpenApiContractTests
{
    private static readonly Dictionary<string, (string Method, string Id, string Schema)> NativeOperations = new(StringComparer.Ordinal)
    {
        ["/api/v1/identity/self"] = ("get", "GetIdentitySelf", "IdentitySelfState"),
        ["/api/v1/identity/self/bootstrap"] = ("post", "BootstrapIdentitySelf", "IdentitySelfActive"),
        ["/api/v1/identity/self/contexts"] = ("get", "ListIdentitySelfContexts", "SelfContextPage"),
        ["/api/v1/identity/session-scope/admissions"] = ("post", "AdmitNativeSessionScope", "NativeSessionScopeAdmission"),
    };

    [Fact]
    public async Task Native_entry_contract_controls_detect_source_downgrade_payload_and_retry_drift()
    {
        var clean = await Served();
        NativeContractErrors(clean).Should().BeEmpty();
        foreach (var (path, descriptor) in NativeOperations)
        {
            var dirty = clean.DeepClone();
            dirty["paths"]![path]![descriptor.Method]!["security"]![0]!.AsObject().Remove("NativeSessionSource");
            NativeContractErrors(dirty).Should().Contain("native-security");
            dirty = clean.DeepClone();
            dirty["paths"]![path]!["head"] = dirty["paths"]![path]![descriptor.Method]!.DeepClone();
            NativeContractErrors(dirty).Should().Contain("native-operations");
            dirty = clean.DeepClone();
            dirty["paths"]![path]![descriptor.Method]!["responses"]!["503"]!["headers"]!["Retry-After"] = new JsonObject { ["required"] = true };
            NativeContractErrors(dirty).Should().Contain("native-retry");
        }
        foreach (var (schema, field) in new[] { ("SelfContextItem", "email"), ("IdentitySelfActive", "tenantId"), ("NativeSessionScopeAdmission", "token") })
        {
            var dirty = clean.DeepClone();
            dirty["components"]!["schemas"]![schema]!["properties"]![field] = new JsonObject { ["type"] = "string" };
            NativeContractErrors(dirty).Should().Contain("native-fields");
        }
        var extraBody = clean.DeepClone();
        extraBody["paths"]!["/api/v1/identity/self/bootstrap"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!["additionalProperties"] = true;
        NativeContractErrors(extraBody).Should().Contain("native-body");
    }

    private static List<string> NativeContractErrors(JsonNode document)
    {
        var errors = new List<string>();
        var scheme = document["components"]?["securitySchemes"]?["NativeSessionSource"];
        if (scheme?["type"]?.GetValue<string>() != "apiKey" || scheme["in"]?.GetValue<string>() != "header"
            || scheme["name"]?.GetValue<string>() != "X-LearnStack-Native-Session-Secret") errors.Add("native-security");
        foreach (var (path, descriptor) in NativeOperations)
        {
            if (document["paths"]?[path] is not JsonObject item || item.Count != 1
                || item[descriptor.Method] is not JsonObject operation) { errors.Add("native-operations"); continue; }
            if (operation["operationId"]?.GetValue<string>() != descriptor.Id) errors.Add("native-operations");
            if (operation["x-version-introduced"]?.GetValue<string>() != "v1") errors.Add("native-version");
            if (operation["security"] is not JsonArray { Count: 1 } security
                || security[0] is not JsonObject { Count: 2 } requirement
                || requirement["TenantBearer"] is not JsonArray { Count: 0 }
                || requirement["NativeSessionSource"] is not JsonArray { Count: 0 }) errors.Add("native-security");
            var bootstrap = descriptor.Id == "BootstrapIdentitySelf";
            if (descriptor.Method == "post")
            {
                var body = operation["requestBody"];
                var schema = body?["content"]?["application/json"]?["schema"];
                if (body?["required"]?.GetValue<bool>() != true || schema?["type"]?.GetValue<string>() != "object"
                    || schema["maxProperties"]?.GetValue<int>() != 0 || schema["additionalProperties"]?.GetValue<bool>() != false)
                    errors.Add("native-body");
            }
            else if (operation.ContainsKey("requestBody")) errors.Add("native-body");
            if (operation["responses"] is not JsonObject responses) { errors.Add("native-responses"); continue; }
            var expected = bootstrap ? Statuses.Concat(["201", "403"]) : Statuses;
            if (!responses.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expected)) errors.Add("native-responses");
            if (responses["503"]?["headers"]?["Retry-After"] is not null) errors.Add("native-retry");
            foreach (var (status, response) in responses)
            {
                if (response?["headers"]?["Cache-Control"]?["schema"]?["enum"]?[0]?.GetValue<string>() != "no-store") errors.Add("native-no-store");
                if (status is "200" or "201")
                {
                    if (response?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/" + descriptor.Schema)
                        errors.Add("native-success");
                }
                else if (response?["content"]?["application/problem+json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/ProblemDetails")
                    errors.Add("native-problem");
            }
        }
        foreach (var (name, fields) in new (string, string[])[]
        {
            ("IdentitySelfMissing", ["state"]), ("IdentitySelfActive", ["state", "authorityId", "subjectKey", "actorId"]),
            ("SelfContextItem", ["tenantId", "organizationId", "tenantName", "organizationName", "entryOrigin", "canUseCurrentHost"]),
            ("SelfContextPage", ["items", "pageInfo"]), ("NativeSessionScopeAdmission", ["actorId", "tenantId", "organizationId", "authorityId", "subjectKey"]),
        })
        {
            var schema = document["components"]?["schemas"]?[name];
            if (schema?["properties"] is not JsonObject properties
                || !properties.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(fields)
                || schema["required"] is not JsonArray required
                || !required.Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(fields)
                || schema["additionalProperties"]?.GetValue<bool>() != false) errors.Add("native-fields");
        }
        return errors;
    }
}

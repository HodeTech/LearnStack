using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Contract;

public sealed partial class OpenApiContractTests
{
    private static readonly Dictionary<string, string> InvitationEntryOperations = new(StringComparer.Ordinal)
    {
        ["/api/v1/identity/invitation-entry/inspections"] = "InspectInvitation",
        ["/api/v1/identity/invitation-entry/acceptances"] = "AcceptInvitation",
        ["/api/v1/identity/invitation-entry/results"] = "GetOwnInvitationResult",
    };
    private static readonly Dictionary<string, string[]> InvitationEntrySchemas = new(StringComparer.Ordinal)
    {
        ["InvitationEntryInput"] = ["invitationId", "code"],
        ["InvitationInspectionResponse"] = ["invitationId", "version", "expiresAt", "tenantLabel", "organizationLabel", "roleId", "roleKey", "roleScope", "verifiedEmail"],
        ["InvitationAcceptanceResponse"] = ["invitationId", "version", "membershipId", "disposition", "tenantId", "organizationId"],
    };
    [Fact]
    public async Task Invitation_entry_served_contract_and_dirty_controls_keep_exact_body_header_and_result_ownership()
    {
        var clean = await Served();
        InvitationEntryContractErrors(clean).Should().BeEmpty();
        foreach (var name in InvitationEntrySchemas.Keys)
        {
            var dirty = clean.DeepClone();
            dirty["components"]!["schemas"]![name]!["properties"]!["tenantSelector"] = new JsonObject { ["type"] = "string" };
            InvitationEntryContractErrors(dirty).Should().Contain("entry-schema");
        }
        foreach (var path in InvitationEntryOperations.Keys)
        {
            var dirty = clean.DeepClone();
            dirty["paths"]![path]!["post"]!["parameters"] = new JsonArray(new JsonObject { ["name"] = "Idempotency-Key", ["in"] = "header" });
            InvitationEntryContractErrors(dirty).Should().Contain("entry-headers");
            dirty = clean.DeepClone();
            dirty["paths"]![path]!["post"]!["security"]![0]!.AsObject().Remove("NativeSessionSource");
            InvitationEntryContractErrors(dirty).Should().Contain("entry-security");
            dirty = clean.DeepClone();
            dirty["paths"]![path]!["post"]!["responses"]!["200"]!["headers"]!["Location"] = new JsonObject();
            InvitationEntryContractErrors(dirty).Should().Contain("entry-success");
        }
    }
    private static List<string> InvitationEntryContractErrors(JsonNode document)
    {
        var errors = new List<string>();
        foreach (var (path, name) in InvitationEntryOperations)
        {
            if (document["paths"]?[path] is not JsonObject methods || methods.Count != 1 || methods["post"] is not JsonObject operation)
            { errors.Add("entry-operations"); continue; }
            if (operation["operationId"]?.GetValue<string>() != name || operation["security"]?[0]?["TenantBearer"] is not JsonArray { Count: 0 }) errors.Add("entry-operations");
            // The runtime also requires the native source credential in the same requirement; the document must say so.
            if (operation["security"] is not JsonArray { Count: 1 } security || security[0] is not JsonObject requirement || requirement.Count != 2
                || requirement["NativeSessionSource"] is not JsonArray { Count: 0 }) errors.Add("entry-security");
            if (operation["requestBody"]?["required"]?.GetValue<bool>() != true || operation["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/InvitationEntryInput") errors.Add("entry-body");
            var parameters = operation["parameters"]?.AsArray() ?? [];
            if (name == "AcceptInvitation" ? parameters.Count != 1 || parameters[0]?["name"]?.GetValue<string>() != "If-Match"
                || parameters[0]?["in"]?.GetValue<string>() != "header" || parameters[0]?["required"]?.GetValue<bool>() == true
                : parameters.Count != 0) errors.Add("entry-headers");
            var success = operation["responses"]?["200"];
            var schema = name == "InspectInvitation" ? "InvitationInspectionResponse" : "InvitationAcceptanceResponse";
            if (success?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/" + schema
                || success?["headers"]?["ETag"]?["required"]?.GetValue<bool>() != true
                || success?["headers"]?["Cache-Control"]?["schema"]?["enum"]?[0]?.GetValue<string>() != "no-store"
                || success?["headers"]?["Location"] is not null || success?["headers"]?["Idempotency-Replayed"] is not null) errors.Add("entry-success");
        }
        foreach (var (name, fields) in InvitationEntrySchemas)
        {
            var schema = document["components"]?["schemas"]?[name];
            if (schema?["additionalProperties"]?.GetValue<bool>() != false || schema["properties"] is not JsonObject properties
                || !properties.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(fields)
                || schema["required"] is not JsonArray required || !required.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            { errors.Add("entry-schema"); continue; }
            if (fields.Contains("version", StringComparer.Ordinal) && properties["version"]?["type"]?.GetValue<string>() != "string") errors.Add("entry-schema");
        }
        return errors;
    }
}

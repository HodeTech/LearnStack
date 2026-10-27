using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;

namespace LearnStack.Tests.Contract;

public sealed partial class OpenApiContractTests
{
    private const string DirectoryPath = "/api/v1/identity/memberships";
    private const string DirectoryPageSchema = "PageOfMembershipDirectoryItem";
    private static readonly string[] DirectorySchemaNames =
        [DirectoryPageSchema, "MembershipDirectoryItem", "DirectoryMembershipStatus"];
    private static readonly string[] DirectoryFields =
        ["membershipId", "userId", "organizationId", "status", "displayName", "preferredLocale"];

    private static List<string> DirectorySchemaErrors(JsonNode document)
    {
        ClrFields(typeof(MembershipDirectoryItem)).Should().BeEquivalentTo(DirectoryFields);
        ClrFields(typeof(Page<MembershipDirectoryItem>)).Should().BeEquivalentTo(["items", "pageInfo"]);
        typeof(MembershipDirectoryItem).GetProperty(nameof(MembershipDirectoryItem.MembershipId))!.PropertyType.Should().Be<Guid>();
        typeof(MembershipDirectoryItem).GetProperty(nameof(MembershipDirectoryItem.UserId))!.PropertyType.Should().Be<UserId>();
        typeof(MembershipDirectoryItem).GetProperty(nameof(MembershipDirectoryItem.OrganizationId))!.PropertyType.Should().Be<OrganizationId?>();
        typeof(MembershipDirectoryItem).GetProperty(nameof(MembershipDirectoryItem.Status))!.PropertyType.Should().Be<DirectoryMembershipStatus>();
        var errors = new List<string>();
        var schemas = document["components"]?["schemas"];
        var item = schemas?["MembershipDirectoryItem"];
        var page = schemas?[DirectoryPageSchema];
        if (!HasExactFields(item, DirectoryFields)) errors.Add("directory-fields");
        if (item?["properties"] is JsonObject fields)
        {
            if (!JsonNode.DeepEquals(fields["membershipId"], new JsonObject { ["type"] = "string", ["format"] = "uuid" })
                || fields["userId"]?["$ref"]?.GetValue<string>() != "#/components/schemas/UserId"
                || fields["organizationId"]?["oneOf"] is not JsonArray { Count: 2 } organization
                || organization[0]?["type"]?.GetValue<string>() != "null"
                || organization[1]?["$ref"]?.GetValue<string>() != "#/components/schemas/OrganizationId") errors.Add("directory-uuid");
            if (fields["status"]?["$ref"]?.GetValue<string>() != "#/components/schemas/DirectoryMembershipStatus"
                || schemas?["DirectoryMembershipStatus"]?["type"]?.GetValue<string>() != "string"
                || schemas?["DirectoryMembershipStatus"]?["enum"] is not JsonArray statuses
                || !statuses.Select(status => status!.GetValue<string>()).SequenceEqual(["Active", "Suspended"])) errors.Add("directory-status");
            foreach (var field in new[] { "displayName", "preferredLocale" })
                if (fields[field]?["type"] is not JsonArray types
                    || !types.Select(type => type!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(["string", "null"])
                    || fields[field]!.AsObject().Count != 1) errors.Add("directory-profile");
        }
        if (!HasExactFields(page, ["items", "pageInfo"])
            || page?["properties"]?["items"]?["type"]?.GetValue<string>() != "array"
            || page?["properties"]?["items"]?["items"]?["$ref"]?.GetValue<string>() != "#/components/schemas/MembershipDirectoryItem"
            || page?["properties"]?["pageInfo"]?["$ref"]?.GetValue<string>() != "#/components/schemas/PageInfo") errors.Add("directory-page");
        return errors;
    }

    private static bool HasExactFields(JsonNode? schema, string[] fields) => schema?["type"]?.GetValue<string>() == "object"
        && schema["properties"] is JsonObject properties
        && properties.Select(property => property.Key).ToHashSet(StringComparer.Ordinal).SetEquals(fields)
        && schema["required"] is JsonArray required
        && required.Select(field => field!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(fields)
        && (schema["additionalProperties"] is null || schema["additionalProperties"] is JsonValue flag
            && flag.TryGetValue<bool>(out var allowed) && !allowed);

    private static void AssertDirectoryControls(JsonNode clean)
    {
        foreach (var mutation in new Action<JsonNode>[]
        {
            value => value["paths"]![DirectoryPath]!.AsObject().Remove("get"),
            value => value["paths"]![DirectoryPath]!["head"] = value["paths"]![DirectoryPath]!["get"]!.DeepClone(),
            value => value["paths"]![DirectoryPath]!["post"] = value["paths"]![DirectoryPath]!["get"]!.DeepClone(),
            value => value["paths"]![DirectoryPath]!["get"]!["operationId"] = "GetAccountSite",
        })
        {
            var dirty = clean.DeepClone(); mutation(dirty);
            ContractErrors(dirty).Should().Contain("operations");
        }
        var changed = clean.DeepClone();
        changed["paths"]![DirectoryPath]!["get"]!.AsObject().Remove("security");
        ContractErrors(changed).Should().Contain("security");
        changed = clean.DeepClone();
        changed["paths"]![DirectoryPath]!["get"]!["responses"]!.AsObject().Remove("403");
        ContractErrors(changed).Should().Contain("responses");
        foreach (var (parameter, keyword, replacement) in new[]
        {
            (0, "maxLength", JsonValue.Create(1025)!),
            (1, "default", JsonValue.Create(100)!),
            (1, "minimum", JsonValue.Create(0)!),
            (1, "maximum", JsonValue.Create(100)!),
        })
        {
            changed = clean.DeepClone();
            changed["paths"]![DirectoryPath]!["get"]!["parameters"]![parameter]!["schema"]![keyword] = replacement.DeepClone();
            ContractErrors(changed).Should().Contain("parameters");
        }
        foreach (var field in new[] { "email", "subject", "roles", "grants", "provider", "tenantId" })
        {
            changed = clean.DeepClone();
            changed["components"]!["schemas"]!["MembershipDirectoryItem"]!["properties"]![field] = new JsonObject { ["type"] = "string" };
            DirectorySchemaErrors(changed).Should().Contain("directory-fields");
        }
        foreach (var (field, error) in new[]
        { ("membershipId", "directory-uuid"), ("userId", "directory-uuid"), ("organizationId", "directory-uuid"),
          ("status", "directory-status"), ("displayName", "directory-profile"), ("preferredLocale", "directory-profile") })
        {
            changed = clean.DeepClone();
            changed["components"]!["schemas"]!["MembershipDirectoryItem"]!["properties"]![field] = new JsonObject { ["type"] = "object" };
            DirectorySchemaErrors(changed).Should().Contain(error);
        }
        changed = clean.DeepClone();
        changed["components"]!["schemas"]![DirectoryPageSchema]!["properties"]!["items"]!["items"]!["$ref"] = "#/components/schemas/AccountSite";
        DirectorySchemaErrors(changed).Should().Contain("directory-page");
        changed = clean.DeepClone();
        changed["components"]!["schemas"]![DirectoryPageSchema]!["additionalProperties"] = true;
        DirectorySchemaErrors(changed).Should().Contain("directory-page");
    }
}

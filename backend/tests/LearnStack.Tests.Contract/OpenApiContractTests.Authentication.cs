using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Contract;

public sealed partial class OpenApiContractTests
{
    private static readonly string[] AccountSchemaNames = ["AccountSite", "UserId", "TenantId", "OrganizationId"];

    [Fact]
    public async Task Authenticated_contract_controls_detect_security_headers_parameters_and_projection_drift()
    {
        var clean = await Served();
        ContractErrors(clean).Should().BeEmpty();
        AccountSchemaErrors(clean).Should().BeEmpty();
        clean["paths"]!["/api/v1/public/site"]!["get"]!["responses"]!["400"]!["description"]!.GetValue<string>()
            .Should().Contain("malformed or repeated assertion headers");
        clean["paths"]!["/api/v1/account/site"]!["get"]!["responses"]!["405"]!["description"]!.GetValue<string>()
            .Should().Contain("after credential and host-backed authority admission").And.Contain("Absent credentials remain masked as 404");

        var dirty = clean.DeepClone();
        dirty["paths"]!["/api/v1/account/site"]!["get"]!.AsObject().Remove("security");
        ContractErrors(dirty).Should().Contain("security");
        dirty = clean.DeepClone();
        dirty["paths"]!["/api/v1/public/site"]!["get"]!["security"] = clean["paths"]!["/api/v1/account/site"]!["get"]!["security"]!.DeepClone();
        ContractErrors(dirty).Should().Contain("public-security");
        dirty = clean.DeepClone();
        dirty["security"] = clean["paths"]!["/api/v1/account/site"]!["get"]!["security"]!.DeepClone();
        ContractErrors(dirty).Should().Contain("security");
        dirty = clean.DeepClone();
        dirty["components"]!["securitySchemes"]!["OtherBearer"] = dirty["components"]!["securitySchemes"]!["TenantBearer"]!.DeepClone();
        ContractErrors(dirty).Should().Contain("security");
        dirty = clean.DeepClone();
        dirty["paths"]!["/api/v1/account/site"]!["get"]!["parameters"] = new JsonArray(
            new JsonObject { ["name"] = "tenantId", ["in"] = "query", ["schema"] = new JsonObject { ["type"] = "string" } });
        ContractErrors(dirty).Should().Contain("parameters");
        dirty = clean.DeepClone();
        dirty["paths"]!["/api/v1/account/site"]!["head"] = dirty["paths"]!["/api/v1/account/site"]!["get"]!.DeepClone();
        ContractErrors(dirty).Should().Contain("operations");
        foreach (var path in new[] { "/api/v1/public/site", "/api/v1/account/site" })
        {
            dirty = clean.DeepClone();
            dirty["paths"]![path]!["get"]!["responses"]!["503"]!["description"] =
                "dependency_unavailable from invalid stored public configuration.";
            ContractErrors(dirty).Should().Contain("subject-cleanup-cause");
            dirty = clean.DeepClone();
            dirty["paths"]![path]!["get"]!["responses"]!["429"]!["description"] =
                "rate_limited from early visitor/peer admission or the validated-subject limiter.";
            ContractErrors(dirty).Should().Contain("subject-capacity-hint");
            dirty = clean.DeepClone();
            dirty["paths"]![path]!["get"]!["responses"]!["429"]!["headers"]!["Retry-After"]!["description"] =
                "Seconds until the first refusing quota permits another attempt.";
            ContractErrors(dirty).Should().Contain("subject-capacity-hint");
            dirty = clean.DeepClone();
            dirty["paths"]![path]!["get"]!["responses"]!.AsObject().Remove("413");
            ContractErrors(dirty).Should().Contain("responses", "the reachable body-limit refusal must stay documented");
            dirty = clean.DeepClone();
            dirty["paths"]![path]!["get"]!["responses"]!["413"]!["headers"]!["Cache-Control"]!["required"] = true;
            ContractErrors(dirty).Should().Contain("body-limit-headers", "the Kestrel backstop can refuse outside application response policy");
            foreach (var (status, header, expected) in new[] { ("401", "WWW-Authenticate", "challenge"), ("429", "Retry-After", "retry-after"), ("503", "Retry-After", "retry-after"), ("405", "Allow", "allow"), ("200", "Cache-Control", "headers") })
            {
                dirty = clean.DeepClone();
                dirty["paths"]![path]!["get"]!["responses"]![status]!["headers"]!.AsObject().Remove(header);
                ContractErrors(dirty).Should().Contain(expected);
            }
        }
        dirty = clean.DeepClone();
        dirty["paths"]!["/api/v1/public/site"]!["head"]!["responses"]!["401"]!["content"] = clean["paths"]!["/api/v1/public/site"]!["get"]!["responses"]!["401"]!["content"]!.DeepClone();
        ContractErrors(dirty).Should().Contain("head-body");
        foreach (var field in new[] { "email", "subject", "roles", "token", "settings" })
        {
            dirty = clean.DeepClone();
            dirty["components"]!["schemas"]!["AccountSite"]!["properties"]![field] = new JsonObject { ["type"] = "string" };
            AccountSchemaErrors(dirty).Should().Contain("account-fields");
        }
        foreach (var field in new[] { "actorId", "tenantId", "organizationId" })
        {
            dirty = clean.DeepClone();
            dirty["components"]!["schemas"]!["AccountSite"]!["properties"]![field] = new JsonObject { ["type"] = "object" };
            AccountSchemaErrors(dirty).Should().Contain("account-uuid");
        }
        foreach (var name in new[] { "UserId", "TenantId", "OrganizationId" })
        {
            dirty = clean.DeepClone();
            dirty["components"]!["schemas"]![name] = new JsonObject { ["type"] = "object" };
            AccountSchemaErrors(dirty).Should().Contain("account-uuid");
        }
        dirty = clean.DeepClone();
        dirty["components"]!["schemas"]!["AccountSite"]!["properties"]!["site"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true };
        AccountSchemaErrors(dirty).Should().Contain("account-site");
        foreach (var additional in new JsonNode[] { JsonValue.Create(true)!, new JsonObject { ["type"] = "string" } })
        {
            dirty = clean.DeepClone();
            dirty["components"]!["schemas"]!["AccountSite"]!["additionalProperties"] = additional;
            AccountSchemaErrors(dirty).Should().Contain("account-additional-properties");
        }
    }

    private static List<string> AccountSchemaErrors(JsonNode document)
    {
        var errors = new List<string>();
        ClrFields(typeof(AccountSite)).Should().BeEquivalentTo(["actorId", "tenantId", "organizationId", "site"]);
        typeof(AccountSite).GetProperty(nameof(AccountSite.ActorId))!.PropertyType.Should().Be<UserId>();
        typeof(AccountSite).GetProperty(nameof(AccountSite.TenantId))!.PropertyType.Should().Be<TenantId>();
        typeof(AccountSite).GetProperty(nameof(AccountSite.OrganizationId))!.PropertyType.Should().Be<OrganizationId?>();
        typeof(AccountSite).GetProperty(nameof(AccountSite.Site))!.PropertyType.Should().Be<PublicSite>();
        var schema = document["components"]?["schemas"]?["AccountSite"];
        var fields = new[] { "actorId", "tenantId", "organizationId", "site" };
        if (schema?["properties"] is not JsonObject properties) return ["account-fields"];
        if (schema["additionalProperties"] is { } additional
            && (additional is not JsonValue flag || !flag.TryGetValue<bool>(out var allowed) || allowed))
            errors.Add("account-additional-properties");
        if (!properties.Select(property => property.Key).ToHashSet(StringComparer.Ordinal).SetEquals(fields)
            || schema["required"] is not JsonArray required
            || !required.Select(field => field!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(fields)) errors.Add("account-fields");
        foreach (var (field, name) in new[] { ("actorId", "UserId"), ("tenantId", "TenantId"), ("organizationId", "OrganizationId") })
        {
            var property = properties[field];
            if (field == "organizationId")
            {
                if (property?["oneOf"] is not JsonArray { Count: 2 } alternatives
                    || alternatives[0]?["type"]?.GetValue<string>() != "null")
                {
                    errors.Add("account-uuid");
                    continue;
                }
                property = alternatives[1];
            }
            var primitive = document["components"]?["schemas"]?[name];
            if (property?["$ref"]?.GetValue<string>() != "#/components/schemas/" + name
                || primitive?["type"]?.GetValue<string>() != "string"
                || primitive["format"]?.GetValue<string>() != "uuid"
                || primitive.AsObject().Count != 2) errors.Add("account-uuid");
        }
        if (properties["site"]?["$ref"]?.GetValue<string>() != "#/components/schemas/PublicSite") errors.Add("account-site");
        return errors;
    }
}

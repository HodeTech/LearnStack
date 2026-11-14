using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Contract;

/// <summary>ADR-0099 Step 2 wire census: protected federation operations without Idempotency-Key, and the sign-in read.</summary>
public sealed partial class OpenApiContractTests
{
    private sealed record FederationOperation(string Path, string Method, string Id, string? Body, bool? IfMatchRequired);

    private static readonly FederationOperation[] FederationOperations =
    [
        new("/api/v1/identity/federation", "get", "GetFederationConnection", null, null),
        new("/api/v1/identity/federation/configuration", "put", "ConfigureFederation", "FederationConfigurationInput", false),
        new("/api/v1/identity/federation/secret-rotations", "post", "RotateFederationSecret", "FederationSecretInput", true),
        new("/api/v1/identity/federation/display-name", "put", "RenameFederation", "FederationDisplayNameInput", true),
        new("/api/v1/identity/federation/test-starts", "post", "StartFederationTest", "EmptyAdministrationInput", true),
        new("/api/v1/identity/federation/test-results", "post", "RecordFederationTestResult", "FederationTestResultInput", true),
        new("/api/v1/identity/federation/activation", "post", "ActivateFederation", "EmptyAdministrationInput", true),
        new("/api/v1/identity/federation/disablement", "post", "DisableFederation", "EmptyAdministrationInput", true),
        new("/api/v1/identity/federation/enablement", "post", "EnableFederation", "EmptyAdministrationInput", true),
        new("/api/v1/identity/federation/deletion", "post", "DeleteFederation", "EmptyAdministrationInput", true),
        new("/api/v1/identity/federation/resumption", "post", "ResumeFederationOperation", "EmptyAdministrationInput", true),
    ];

    private static readonly string[] FederationSchemaNames =
    [
        "FederationOperationResponse", "FederationPendingResponse", "FederationConnectionBody", "FederationConnectionResponse",
        "FederationConfigurationInput", "FederationSecretInput", "FederationDisplayNameInput", "FederationTestResultInput",
        "PublicSignIn", "PublicSignInFederation",
    ];

    private static readonly string[] FederationSecretInputs = ["FederationConfigurationInput", "FederationSecretInput"];

    private static List<string> FederationContractErrors(JsonNode document)
    {
        var errors = new List<string>();
        foreach (var group in FederationOperations.GroupBy(operation => operation.Path, StringComparer.Ordinal))
            if (document["paths"]?[group.Key] is not JsonObject item
                || !item.Select(method => method.Key).ToHashSet(StringComparer.Ordinal).SetEquals(group.Select(operation => operation.Method)))
                errors.Add("federation-operations");
        foreach (var contract in FederationOperations)
        {
            if (document["paths"]?[contract.Path]?[contract.Method] is not JsonObject operation) { errors.Add("federation-operations"); continue; }
            if (operation["operationId"]?.GetValue<string>() != contract.Id) errors.Add("federation-operations");
            if (operation["security"] is not JsonArray { Count: 1 } security || security[0] is not JsonObject { Count: 1 } requirement
                || requirement["TenantBearer"] is not JsonArray { Count: 0 }) errors.Add("federation-security");
            var parameters = operation["parameters"]?.AsArray() ?? [];
            if (parameters.Any(parameter => parameter?["name"]?.GetValue<string>() == "Idempotency-Key")) errors.Add("federation-idempotency");
            var ifMatch = parameters.SingleOrDefault(parameter => parameter?["name"]?.GetValue<string>() == "If-Match");
            if (contract.IfMatchRequired is { } required
                ? ifMatch is null || (ifMatch["required"]?.GetValue<bool>() ?? false) != required || parameters.Count != 1
                : parameters.Count != 0) errors.Add("federation-precondition");
            if ((contract.Body is null) != (operation["requestBody"] is null)
                || contract.Body is not null && operation["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>()
                    != "#/components/schemas/" + contract.Body) errors.Add("federation-body");
            var success = contract.Body is null ? "FederationConnectionResponse" : "FederationOperationResponse";
            if (operation["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/" + success)
                errors.Add("federation-success");
        }
        foreach (var name in FederationSchemaNames)
            if (document["components"]?["schemas"]?[name] is not JsonObject schema || schema["additionalProperties"]?.GetValue<bool>() != false)
                errors.Add("federation-schema:" + name);
        // The secret is write-only on both inputs that carry it and is a property of no other federation schema, so no
        // response or read DTO can return it.
        if (FederationSecretInputs.Any(name => document["components"]?["schemas"]?[name]?["properties"]?["clientSecret"]?["writeOnly"]
                ?.GetValue<bool>() != true)
            || FederationSchemaNames.Except(FederationSecretInputs, StringComparer.Ordinal)
                .Any(name => document["components"]?["schemas"]?[name]?["properties"]?["clientSecret"] is not null))
            errors.Add("federation-secret");
        return errors;
    }

    [Fact]
    public async Task Federation_census_refuses_an_idempotency_key_a_missing_precondition_and_a_readable_secret()
    {
        var clean = await Served();
        FederationContractErrors(clean).Should().BeEmpty();
        var keyed = clean.DeepClone();
        keyed["paths"]!["/api/v1/identity/federation/deletion"]!["post"]!["parameters"]!.AsArray()
            .Add(new JsonObject { ["name"] = "Idempotency-Key", ["in"] = "header" });
        FederationContractErrors(keyed).Should().Contain("federation-idempotency");
        var unconditional = clean.DeepClone();
        unconditional["paths"]!["/api/v1/identity/federation/activation"]!["post"]!["parameters"] = new JsonArray();
        FederationContractErrors(unconditional).Should().Contain("federation-precondition");
        var readable = clean.DeepClone();
        readable["components"]!["schemas"]!["FederationSecretInput"]!["properties"]!["clientSecret"]!.AsObject().Remove("writeOnly");
        FederationContractErrors(readable).Should().Contain("federation-secret");
        var readableConfiguration = clean.DeepClone();
        readableConfiguration["components"]!["schemas"]!["FederationConfigurationInput"]!["properties"]!["clientSecret"]!.AsObject().Remove("writeOnly");
        FederationContractErrors(readableConfiguration).Should().Contain("federation-secret");
        var returned = clean.DeepClone();
        returned["components"]!["schemas"]!["FederationConnectionBody"]!["properties"]!.AsObject()
            .Add("clientSecret", new JsonObject { ["type"] = "string" });
        FederationContractErrors(returned).Should().Contain("federation-secret");
    }
}

using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Contract;

/// <summary>
/// ADR-0106 § 10 and ADR-0107 § 4 wire census: the keyed 202 creations, the conditional keyed mutations, and the ticket
/// operations that are neither keyed nor conditional and carry the ticket only in a body.
/// </summary>
public sealed partial class OpenApiContractTests
{
    private enum DataRequestShape { Read, Create, Mutation, Ticket }

    private sealed record DataRequestOperation(string Path, string Method, string Id, DataRequestShape Shape, string Response, string? Body);

    private static readonly DataRequestOperation[] DataRequestOperations =
    [
        new("/api/v1/identity/me/data-requests/exports", "post", "CreateOwnDataExport", DataRequestShape.Create, "DataRequestAcceptedResponse", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests", "get", "ListOwnDataRequests", DataRequestShape.Read, "OwnDataRequestPage", null),
        new("/api/v1/identity/me/data-requests/{id}", "get", "GetOwnDataRequest", DataRequestShape.Read, "OwnDataRequestResponse", null),
        new("/api/v1/identity/me/data-requests/{id}/cancellations", "post", "CancelOwnDataRequest", DataRequestShape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests/{id}/artifact/revocations", "post", "RevokeOwnDataExport", DataRequestShape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests/{id}/artifact/tickets", "post", "MintOwnDataExportTicket", DataRequestShape.Ticket, "DataExportTicketResponse", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests/{id}/artifact/chunks", "post", "ReadOwnDataExportChunk", DataRequestShape.Ticket, "DataExportChunkResponse", "DataExportChunkInput"),
        new("/api/v1/identity/data-requests/exports", "post", "CreateDataExportForSubject", DataRequestShape.Create, "DataRequestAcceptedResponse", "DataExportForSubjectInput"),
        new("/api/v1/identity/data-requests", "get", "ListDataRequests", DataRequestShape.Read, "DataRequestPage", null),
        new("/api/v1/identity/data-requests/{id}", "get", "GetDataRequest", DataRequestShape.Read, "DataRequestResponse", null),
        new("/api/v1/identity/data-requests/{id}/cancellations", "post", "CancelDataRequest", DataRequestShape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput"),
        new("/api/v1/identity/data-requests/{id}/artifact/revocations", "post", "RevokeDataExport", DataRequestShape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput"),
    ];

    private static readonly string[] DataRequestSchemaNames =
    [
        "DataRequestAcceptedResponse", "DataRequestRetainedResponse", "DataExportTruncationResponse", "DataExportArtifactResponse",
        "OwnDataRequestResponse", "OwnDataRequestPage", "DataRequestResponse", "DataRequestPage", "DataExportForSubjectInput",
        "DataExportTicketResponse", "DataExportChunkInput", "DataExportChunkResponse",
    ];

    private static List<string> DataRequestContractErrors(JsonNode document)
    {
        var errors = new List<string>();
        foreach (var group in DataRequestOperations.GroupBy(operation => operation.Path, StringComparer.Ordinal))
            if (document["paths"]?[group.Key] is not JsonObject item
                || !item.Select(method => method.Key).ToHashSet(StringComparer.Ordinal).SetEquals(group.Select(operation => operation.Method)))
                errors.Add("data-request-operations");
        foreach (var contract in DataRequestOperations)
        {
            if (document["paths"]?[contract.Path]?[contract.Method] is not JsonObject operation) { errors.Add("data-request-operations"); continue; }
            if (operation["operationId"]?.GetValue<string>() != contract.Id) errors.Add("data-request-operations");
            if (operation["security"] is not JsonArray { Count: 1 } security || security[0] is not JsonObject { Count: 1 } requirement
                || requirement["TenantBearer"] is not JsonArray { Count: 0 }) errors.Add("data-request-security");
            var parameters = (operation["parameters"]?.AsArray() ?? []).Select(parameter => parameter!["name"]!.GetValue<string>()).ToArray();
            var expected = new List<string>();
            if (contract.Path.Contains("{id}", StringComparison.Ordinal)) expected.Add("id");
            if (contract.Shape == DataRequestShape.Read && !contract.Path.EndsWith("{id}", StringComparison.Ordinal)) expected.AddRange(["cursor", "limit"]);
            if (contract.Shape is DataRequestShape.Create or DataRequestShape.Mutation) expected.Add("Idempotency-Key");
            if (contract.Shape == DataRequestShape.Mutation) expected.Add("If-Match");
            if (!parameters.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                errors.Add("data-request-parameters:" + contract.Id);
            if (parameters.Any(name => name.Contains("ticket", StringComparison.OrdinalIgnoreCase))) errors.Add("data-request-ticket-in-url");
            if ((contract.Body is null) != (operation["requestBody"] is null)
                || contract.Body is not null && operation["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>()
                    != "#/components/schemas/" + contract.Body) errors.Add("data-request-body:" + contract.Id);
            var status = contract.Shape == DataRequestShape.Create ? "202" : "200";
            var responses = operation["responses"]!.AsObject();
            if (responses[status]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/" + contract.Response
                || responses.Any(response => response.Key is "200" or "201" or "202" && response.Key != status))
                errors.Add("data-request-success:" + contract.Id);
            foreach (var (_, response) in responses)
                if (response?["headers"]?["Cache-Control"]?["schema"]?["enum"]?[0]?.GetValue<string>() != "no-store") errors.Add("data-request-no-store:" + contract.Id);
            var etag = responses[status]?["headers"]?["ETag"];
            if ((contract.Shape is DataRequestShape.Create or DataRequestShape.Mutation
                    || contract.Shape == DataRequestShape.Read && contract.Path.EndsWith("{id}", StringComparison.Ordinal)) != (etag is not null))
                errors.Add("data-request-etag:" + contract.Id);
            if ((contract.Shape == DataRequestShape.Create) != (responses[status]?["headers"]?["Location"] is not null))
                errors.Add("data-request-location:" + contract.Id);
        }
        foreach (var name in DataRequestSchemaNames)
            if (document["components"]?["schemas"]?[name] is not JsonObject schema || schema["additionalProperties"]?.GetValue<bool>() != false)
                errors.Add("data-request-schema:" + name);
        // The subject's receipt names no internal identifier; only the administrator's view names the frozen Memberships.
        if (document["components"]?["schemas"]?["OwnDataRequestResponse"]?["properties"]?["membershipIds"] is not null
            || document["components"]?["schemas"]?["DataRequestResponse"]?["properties"]?["membershipIds"] is null)
            errors.Add("data-request-membership-disclosure");
        // No receipt or artifact view carries a key label or content.
        foreach (var name in DataRequestSchemaNames)
            if (document["components"]?["schemas"]?[name]?["properties"] is JsonObject properties
                && properties.Any(property => property.Key is "keyVersion" or "ciphertext" or "nonce" or "digest" or "subjectUserId"))
                errors.Add("data-request-disclosure:" + name);
        return errors;
    }

    [Fact]
    public async Task Data_request_census_refuses_a_keyed_ticket_a_ticket_in_the_url_and_a_missing_precondition()
    {
        var clean = await Served();
        DataRequestContractErrors(clean).Should().BeEmpty();

        var keyed = clean.DeepClone();
        keyed["paths"]!["/api/v1/identity/me/data-requests/{id}/artifact/tickets"]!["post"]!["parameters"]!.AsArray()
            .Add(new JsonObject { ["name"] = "Idempotency-Key", ["in"] = "header" });
        DataRequestContractErrors(keyed).Should().Contain("data-request-parameters:MintOwnDataExportTicket");

        var query = clean.DeepClone();
        query["paths"]!["/api/v1/identity/me/data-requests/{id}/artifact/chunks"]!["post"]!["parameters"]!.AsArray()
            .Add(new JsonObject { ["name"] = "ticket", ["in"] = "query" });
        DataRequestContractErrors(query).Should().Contain("data-request-ticket-in-url");

        var unconditional = clean.DeepClone();
        var cancel = unconditional["paths"]!["/api/v1/identity/me/data-requests/{id}/cancellations"]!["post"]!["parameters"]!.AsArray();
        cancel.Remove(cancel.Single(parameter => parameter!["name"]!.GetValue<string>() == "If-Match"));
        DataRequestContractErrors(unconditional).Should().Contain("data-request-parameters:CancelOwnDataRequest");

        var disclosed = clean.DeepClone();
        disclosed["components"]!["schemas"]!["OwnDataRequestResponse"]!["properties"]!["membershipIds"] = new JsonObject { ["type"] = "array" };
        DataRequestContractErrors(disclosed).Should().Contain("data-request-membership-disclosure");
    }
}

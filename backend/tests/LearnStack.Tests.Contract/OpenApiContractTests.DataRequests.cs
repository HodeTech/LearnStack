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
        new("/api/v1/identity/me/data-requests/erasures", "post", "CreateOwnDataErasure", DataRequestShape.Create, "DataErasureAcceptedResponse", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests/closures", "post", "CreateOwnAccountClosure", DataRequestShape.Create, "AccountClosureAcceptedResponse", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests", "get", "ListOwnDataRequests", DataRequestShape.Read, "OwnDataRequestPage", null),
        new("/api/v1/identity/me/data-requests/{id}", "get", "GetOwnDataRequest", DataRequestShape.Read, "OwnDataRequestResponse", null),
        new("/api/v1/identity/me/data-requests/{id}/cancellations", "post", "CancelOwnDataRequest", DataRequestShape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests/{id}/artifact/revocations", "post", "RevokeOwnDataExport", DataRequestShape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests/{id}/artifact/tickets", "post", "MintOwnDataExportTicket", DataRequestShape.Ticket, "DataExportTicketResponse", "EmptyAdministrationInput"),
        new("/api/v1/identity/me/data-requests/{id}/artifact/chunks", "post", "ReadOwnDataExportChunk", DataRequestShape.Ticket, "DataExportChunkResponse", "DataExportChunkInput"),
        new("/api/v1/identity/data-requests/exports", "post", "CreateDataExportForSubject", DataRequestShape.Create, "DataRequestAcceptedResponse", "DataExportForSubjectInput"),
        new("/api/v1/identity/data-requests/erasures", "post", "CreateDataErasureForSubject", DataRequestShape.Create, "DataErasureAcceptedResponse", "DataExportForSubjectInput"),
        new("/api/v1/identity/data-requests", "get", "ListDataRequests", DataRequestShape.Read, "DataRequestPage", null),
        new("/api/v1/identity/data-requests/{id}", "get", "GetDataRequest", DataRequestShape.Read, "DataRequestResponse", null),
        new("/api/v1/identity/data-requests/{id}/cancellations", "post", "CancelDataRequest", DataRequestShape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput"),
        new("/api/v1/identity/data-requests/{id}/artifact/revocations", "post", "RevokeDataExport", DataRequestShape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput"),
    ];

    // ADR-0106 § 9: the retained own-scope native entry. Both credentials on one requirement, a required group on every
    // purpose but discovery, a key only on creation, cancellation and revocation, If-Match only on cancellation and
    // revocation, and no Location.
    private static readonly DataRequestOperation[] RetainedDataRequestOperations =
    [
        new("/api/v1/identity/data-request-entry/scopes", "get", "ListRetainedDataRequestScopes", DataRequestShape.Read, "RetainedDataRequestScopePageResponse", null),
        new("/api/v1/identity/data-request-entry/requests", "get", "ListRetainedDataRequests", DataRequestShape.Read, "OwnDataRequestPage", null),
        new("/api/v1/identity/data-request-entry/exports", "post", "CreateRetainedDataExport", DataRequestShape.Create, "DataRequestAcceptedResponse", "EmptyAdministrationInput"),
        new("/api/v1/identity/data-request-entry/erasures", "post", "CreateRetainedDataErasure", DataRequestShape.Create, "DataErasureAcceptedResponse", "EmptyAdministrationInput"),
        new("/api/v1/identity/data-request-entry/closures", "post", "CreateRetainedAccountClosure", DataRequestShape.Create, "AccountClosureAcceptedResponse", "EmptyAdministrationInput"),
        new("/api/v1/identity/data-request-entry/cancellations", "post", "CancelRetainedDataRequest", DataRequestShape.Mutation, "AdministrationVersionReceipt", "RetainedDataRequestTargetInput"),
        new("/api/v1/identity/data-request-entry/revocations", "post", "RevokeRetainedDataExport", DataRequestShape.Mutation, "AdministrationVersionReceipt", "RetainedDataRequestTargetInput"),
        new("/api/v1/identity/data-request-entry/tickets", "post", "MintRetainedDataExportTicket", DataRequestShape.Ticket, "DataExportTicketResponse", "RetainedDataRequestTargetInput"),
        new("/api/v1/identity/data-request-entry/chunks", "post", "ReadRetainedDataExportChunk", DataRequestShape.Ticket, "DataExportChunkResponse", "RetainedDataExportChunkInput"),
    ];

    private static readonly string[] RetainedDataRequestSchemaNames =
        ["RetainedDataRequestScopeResponse", "RetainedDataRequestScopePageResponse", "RetainedDataRequestTargetInput", "RetainedDataExportChunkInput"];

    private static List<string> RetainedDataRequestContractErrors(JsonNode document)
    {
        var errors = new List<string>();
        foreach (var contract in RetainedDataRequestOperations)
        {
            if (document["paths"]?[contract.Path] is not JsonObject item || item.Count != 1
                || item[contract.Method] is not JsonObject operation) { errors.Add("retained-data-request-operations"); continue; }
            if (operation["operationId"]?.GetValue<string>() != contract.Id) errors.Add("retained-data-request-operations");
            if (operation["security"] is not JsonArray { Count: 1 } security || security[0] is not JsonObject { Count: 2 } requirement
                || requirement["TenantBearer"] is not JsonArray { Count: 0 } || requirement["NativeSessionSource"] is not JsonArray { Count: 0 })
                errors.Add("retained-data-request-security:" + contract.Id);
            var parameters = (operation["parameters"]?.AsArray() ?? []).Select(parameter => parameter!["name"]!.GetValue<string>()).ToArray();
            var expected = new List<string>();
            var discovery = contract.Id == "ListRetainedDataRequestScopes";
            if (!discovery) expected.Add("group");
            if (contract.Shape == DataRequestShape.Read) expected.AddRange(["cursor", "limit"]);
            if (contract.Shape is DataRequestShape.Create or DataRequestShape.Mutation) expected.Add("Idempotency-Key");
            if (contract.Shape == DataRequestShape.Mutation) expected.Add("If-Match");
            if (!parameters.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                errors.Add("retained-data-request-parameters:" + contract.Id);
            // A revocation's precondition is its artifact's version; every other mutation's is the request's.
            if (contract.Shape == DataRequestShape.Mutation
                && (operation["parameters"]!.AsArray().FirstOrDefault(parameter => parameter!["name"]!.GetValue<string>() == "If-Match")?["description"]?.GetValue<string>() ?? "")
                    .Contains("ARTIFACT", StringComparison.Ordinal) != contract.Id.StartsWith("Revoke", StringComparison.Ordinal))
                errors.Add("retained-data-request-if-match-version:" + contract.Id);
            if (parameters.Any(name => name.Contains("ticket", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-LearnStack", StringComparison.Ordinal)))
                errors.Add("retained-data-request-parameter-leak:" + contract.Id);
            if ((contract.Body is null) != (operation["requestBody"] is null)
                || contract.Body is not null && operation["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>()
                    != "#/components/schemas/" + contract.Body) errors.Add("retained-data-request-body:" + contract.Id);
            var status = contract.Shape == DataRequestShape.Create ? "202" : "200";
            var responses = operation["responses"]!.AsObject();
            if (responses[status]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/" + contract.Response
                || responses.Any(response => response.Key is "200" or "201" or "202" && response.Key != status))
                errors.Add("retained-data-request-success:" + contract.Id);
            foreach (var (_, response) in responses)
                if (response?["headers"]?["Cache-Control"]?["schema"]?["enum"]?[0]?.GetValue<string>() != "no-store")
                    errors.Add("retained-data-request-no-store:" + contract.Id);
            if ((contract.Shape is DataRequestShape.Create or DataRequestShape.Mutation) != (responses[status]?["headers"]?["ETag"] is not null))
                errors.Add("retained-data-request-etag:" + contract.Id);
            if (responses[status]?["headers"]?["Location"] is not null) errors.Add("retained-data-request-location:" + contract.Id);
        }
        foreach (var name in RetainedDataRequestSchemaNames)
            if (document["components"]?["schemas"]?[name] is not JsonObject schema || schema["additionalProperties"]?.GetValue<bool>() != false)
                errors.Add("retained-data-request-schema:" + name);
        return errors;
    }

    [Fact]
    public async Task Retained_data_request_census_refuses_a_lost_native_credential_group_or_precondition_and_a_location()
    {
        var clean = await Served();
        RetainedDataRequestContractErrors(clean).Should().BeEmpty();

        var bearerOnly = clean.DeepClone();
        bearerOnly["paths"]!["/api/v1/identity/data-request-entry/requests"]!["get"]!["security"]![0]!.AsObject().Remove("NativeSessionSource");
        RetainedDataRequestContractErrors(bearerOnly).Should().Contain("retained-data-request-security:ListRetainedDataRequests");

        var groupless = clean.DeepClone();
        var tickets = groupless["paths"]!["/api/v1/identity/data-request-entry/tickets"]!["post"]!["parameters"]!.AsArray();
        tickets.Remove(tickets.Single(parameter => parameter!["name"]!.GetValue<string>() == "group"));
        RetainedDataRequestContractErrors(groupless).Should().Contain("retained-data-request-parameters:MintRetainedDataExportTicket");

        var selector = clean.DeepClone();
        selector["paths"]!["/api/v1/identity/data-request-entry/chunks"]!["post"]!["parameters"]!.AsArray()
            .Add(new JsonObject { ["name"] = "X-LearnStack-Native-Tenant-Id", ["in"] = "header" });
        RetainedDataRequestContractErrors(selector).Should().Contain("retained-data-request-parameter-leak:ReadRetainedDataExportChunk");

        var unconditional = clean.DeepClone();
        var cancel = unconditional["paths"]!["/api/v1/identity/data-request-entry/cancellations"]!["post"]!["parameters"]!.AsArray();
        cancel.Remove(cancel.Single(parameter => parameter!["name"]!.GetValue<string>() == "If-Match"));
        RetainedDataRequestContractErrors(unconditional).Should().Contain("retained-data-request-parameters:CancelRetainedDataRequest");

        var located = clean.DeepClone();
        located["paths"]!["/api/v1/identity/data-request-entry/exports"]!["post"]!["responses"]!["202"]!["headers"]!.AsObject()
            .Add("Location", new JsonObject { ["required"] = true });
        RetainedDataRequestContractErrors(located).Should().Contain("retained-data-request-location:CreateRetainedDataExport");
    }

    private static readonly string[] DataRequestSchemaNames =
    [
        "DataRequestAcceptedResponse", "DataErasureAcceptedResponse", "AccountClosureAcceptedResponse", "DataRequestRetainedResponse",
        "DataExportTruncationResponse",
        "DataExportArtifactResponse",
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
            // A revocation's precondition is its artifact's version; every other mutation's is the request's.
            if (contract.Shape == DataRequestShape.Mutation
                && (operation["parameters"]!.AsArray().FirstOrDefault(parameter => parameter!["name"]!.GetValue<string>() == "If-Match")?["description"]?.GetValue<string>() ?? "")
                    .Contains("ARTIFACT", StringComparison.Ordinal) != contract.Id.StartsWith("Revoke", StringComparison.Ordinal))
                errors.Add("data-request-if-match-version:" + contract.Id);
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
        // ADR-0107 § 7: the person alone files a closure; its accepted resource is a scheduled closure and nothing else, and
        // the administrator's surface has no closure route.
        if (document["components"]?["schemas"]?["AccountClosureAcceptedResponse"]?["properties"] is not JsonObject closure
            || closure["kind"]?["enum"] is not JsonArray { Count: 1 } kind || kind[0]?.GetValue<string>() != "closure"
            || closure["status"]?["enum"] is not JsonArray { Count: 1 } scheduled || scheduled[0]?.GetValue<string>() != "scheduled"
            || document["paths"]?.AsObject().Any(path => path.Key.StartsWith("/api/v1/identity/data-requests", StringComparison.Ordinal)
                && path.Key.Contains("closure", StringComparison.Ordinal)) != false)
            errors.Add("data-request-closure");
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

        var widened = clean.DeepClone();
        widened["components"]!["schemas"]!["AccountClosureAcceptedResponse"]!["properties"]!["kind"]!["enum"]!.AsArray().Add("erasure");
        DataRequestContractErrors(widened).Should().Contain("data-request-closure");

        var administered = clean.DeepClone();
        administered["paths"]!.AsObject().Add("/api/v1/identity/data-requests/closures",
            clean["paths"]!["/api/v1/identity/me/data-requests/closures"]!.DeepClone());
        DataRequestContractErrors(administered).Should().Contain("data-request-closure");

        var disclosed = clean.DeepClone();
        disclosed["components"]!["schemas"]!["OwnDataRequestResponse"]!["properties"]!["membershipIds"] = new JsonObject { ["type"] = "array" };
        DataRequestContractErrors(disclosed).Should().Contain("data-request-membership-disclosure");
    }
}

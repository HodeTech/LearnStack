using System.Text.Json.Nodes;
using LearnStack.Api.Authentication;
using LearnStack.Api.Idempotency;
using LearnStack.Api.PublicReads;
using LearnStack.SharedKernel.DataProtection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Identity;

/// <summary>
/// The exact wire contract of the live-session data-request API (ADR-0106 § 10, ADR-0107 § 4): the subject's own
/// requests, their artifact and its ticketed download under <c>identity/me/data-requests</c>, and the administrator's
/// status-only view under <c>identity/data-requests</c>. Request bodies are read without MVC binding.
/// </summary>
internal sealed class DataRequestDocumentTransformer : IOpenApiDocumentTransformer
{
    private enum Shape { Read, Page, Create, Mutation, Ticket, Chunk }

    private static readonly string[] SuccessStatuses = ["200", "201", "202"];

    /// <summary>What a client does with a ticket or chunk refusal (ADR-0107 § 4).</summary>
    private const string DownloadRecovery = " A ticket is bound to the credential that minted it, so a not_found during a download may only mean the "
        + "session credential changed (a refresh or a new sign-in): mint a new ticket and resume from the index that failed. If that mint "
        + "answers 409 with data_export_unavailable, the artifact is gone (revoked, expired, purged, or sealed under a key the export key "
        + "ring no longer holds) and only a new export helps; with data_export_ticket_limit, a ticket bound is reached (see the mint's 409).";

    /// <summary>The assembled bundle's shape, stated on both chunk routes.</summary>
    private const string BundleShape = "Decoding every chunk's bytes and concatenating them in index order yields the bundle: one UTF-8 JSON "
        + "document, served as application/json; charset=utf-8, whose top-level member schema_version names its format.";

    private sealed record Contract(string Path, HttpMethod Method, string Name, Shape Shape, string Response, string? Body, bool Own);

    private static readonly Contract[] Contracts =
    [
        new("identity/me/data-requests/exports", HttpMethod.Post, "CreateOwnDataExport", Shape.Create, "DataRequestAcceptedResponse", "EmptyAdministrationInput", true),
        new("identity/me/data-requests/erasures", HttpMethod.Post, "CreateOwnDataErasure", Shape.Create, "DataErasureAcceptedResponse", "EmptyAdministrationInput", true),
        new("identity/me/data-requests", HttpMethod.Get, "ListOwnDataRequests", Shape.Page, "OwnDataRequestPage", null, true),
        new("identity/me/data-requests/{id}", HttpMethod.Get, "GetOwnDataRequest", Shape.Read, "OwnDataRequestResponse", null, true),
        new("identity/me/data-requests/{id}/cancellations", HttpMethod.Post, "CancelOwnDataRequest", Shape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput", true),
        new("identity/me/data-requests/{id}/artifact/revocations", HttpMethod.Post, "RevokeOwnDataExport", Shape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput", true),
        new("identity/me/data-requests/{id}/artifact/tickets", HttpMethod.Post, "MintOwnDataExportTicket", Shape.Ticket, "DataExportTicketResponse", "EmptyAdministrationInput", true),
        new("identity/me/data-requests/{id}/artifact/chunks", HttpMethod.Post, "ReadOwnDataExportChunk", Shape.Chunk, "DataExportChunkResponse", "DataExportChunkInput", true),
        new("identity/data-requests/exports", HttpMethod.Post, "CreateDataExportForSubject", Shape.Create, "DataRequestAcceptedResponse", "DataExportForSubjectInput", false),
        new("identity/data-requests/erasures", HttpMethod.Post, "CreateDataErasureForSubject", Shape.Create, "DataErasureAcceptedResponse", "DataExportForSubjectInput", false),
        new("identity/data-requests", HttpMethod.Get, "ListDataRequests", Shape.Page, "DataRequestPage", null, false),
        new("identity/data-requests/{id}", HttpMethod.Get, "GetDataRequest", Shape.Read, "DataRequestResponse", null, false),
        new("identity/data-requests/{id}/cancellations", HttpMethod.Post, "CancelDataRequest", Shape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput", false),
        new("identity/data-requests/{id}/artifact/revocations", HttpMethod.Post, "RevokeDataExport", Shape.Mutation, "AdministrationVersionReceipt", "EmptyAdministrationInput", false),
    ];

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (context.DocumentName != "v1" || !document.Paths.ContainsKey("/api/v1/identity/me/data-requests")) return Task.CompletedTask;
        document.Components ??= new();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        Schemas(document);
        foreach (var contract in Contracts)
        {
            if (!document.Paths.TryGetValue("/api/v1/" + contract.Path, out var path)
                || path.Operations?.TryGetValue(contract.Method, out var operation) != true || operation is null) continue;
            Describe(document, path, operation, contract);
        }
        foreach (var contract in RetainedContracts)
        {
            if (!document.Paths.TryGetValue("/api/v1/" + contract.Path, out var path)
                || path.Operations?.TryGetValue(contract.Method, out var operation) != true || operation is null) continue;
            DescribeRetained(document, path, operation, contract);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// ADR-0106 § 9: the retained own-scope native entry. Selector-free (no tenant or organization header), reached only
    /// with the native session source credential, every purpose but discovery naming its one group in <c>group</c>.
    /// </summary>
    private static readonly Contract[] RetainedContracts =
    [
        new("identity/data-request-entry/scopes", HttpMethod.Get, "ListRetainedDataRequestScopes", Shape.Page, "RetainedDataRequestScopePageResponse", null, true),
        new("identity/data-request-entry/requests", HttpMethod.Get, "ListRetainedDataRequests", Shape.Page, "OwnDataRequestPage", null, true),
        new("identity/data-request-entry/exports", HttpMethod.Post, "CreateRetainedDataExport", Shape.Create, "DataRequestAcceptedResponse", "EmptyAdministrationInput", true),
        new("identity/data-request-entry/erasures", HttpMethod.Post, "CreateRetainedDataErasure", Shape.Create, "DataErasureAcceptedResponse", "EmptyAdministrationInput", true),
        new("identity/data-request-entry/cancellations", HttpMethod.Post, "CancelRetainedDataRequest", Shape.Mutation, "AdministrationVersionReceipt", "RetainedDataRequestTargetInput", true),
        new("identity/data-request-entry/revocations", HttpMethod.Post, "RevokeRetainedDataExport", Shape.Mutation, "AdministrationVersionReceipt", "RetainedDataRequestTargetInput", true),
        new("identity/data-request-entry/tickets", HttpMethod.Post, "MintRetainedDataExportTicket", Shape.Ticket, "DataExportTicketResponse", "RetainedDataRequestTargetInput", true),
        new("identity/data-request-entry/chunks", HttpMethod.Post, "ReadRetainedDataExportChunk", Shape.Chunk, "DataExportChunkResponse", "RetainedDataExportChunkInput", true),
    ];

    private static void DescribeRetained(OpenApiDocument document, IOpenApiPathItem path, OpenApiOperation operation, Contract contract)
    {
        operation.OperationId = contract.Name;
        operation.Security = [new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(TenantAuthenticationRegistration.Scheme, document)] = [],
            [new OpenApiSecuritySchemeReference(NativeEntryDocumentTransformer.SourceScheme, document)] = [],
        }];
        operation.Description = RetainedDescription(contract);
        operation.Parameters = [];
        var discovery = contract.Name == "ListRetainedDataRequestScopes";
        if (!discovery) operation.Parameters.Add(new OpenApiParameter
        {
            Name = "group",
            In = ParameterLocation.Query,
            Required = true,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MinLength = 6,
                MaxLength = 36,
                Pattern = "^(tenant|[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})$",
            },
            Description = "The one retained group the operation acts in: tenant for the real host's tenant-wide group, or the organization's canonical UUIDv7. On an organization host only that organization. A group the subject does not retain is not_found.",
        });
        if (contract.Shape == Shape.Page)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "cursor",
                In = ParameterLocation.Query,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = discovery ? 36 : 1024 },
                Description = discovery
                    ? "The previous page's nextCursor: the last group's key (tenant or an organization UUIDv7)."
                    : "Opaque canonical current-actor/group/operation-bound cursor. Unknown, repeated, blank and oversized values refuse.",
            });
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "limit",
                In = ParameterLocation.Query,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "20", Default = JsonValue.Create(20) },
                Description = "Canonical positive ASCII decimal Int32 from 1 through 20, default 20. Signs, leading zeros, repeats and larger values refuse. Paging is forward only: follow pageInfo.nextCursor; there is no previous page.",
            });
        }
        if (contract.Body is not null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = Ref(document, contract.Body) } },
            };
            if (contract.Shape is Shape.Create or Shape.Mutation) operation.Parameters.Add(new OpenApiParameter
            {
                Name = IdempotentAttribute.HeaderName,
                In = ParameterLocation.Header,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 8, MaxLength = 128, Pattern = "^[!-~]{8,128}$" },
                Description = "Exactly one printable ASCII nonce, scoped to actor, tenant/organization, operation, method/path/query/body and canonical precondition. No automatic mutation retry.",
            });
            if (contract.Shape == Shape.Mutation) operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-Match",
                In = ParameterLocation.Header,
                Required = true,
                Schema = EntityTag(),
                Description = IfMatchDescription(contract),
            });
        }
        else operation.RequestBody = null;

        operation.Responses ??= [];
        var status = contract.Shape == Shape.Create ? "202" : "200";
        foreach (var other in SuccessStatuses.Where(code => code != status)) operation.Responses.Remove(other);
        operation.Responses[status] = new OpenApiResponse
        {
            Description = contract.Shape switch
            {
                Shape.Create => Erasure(contract)
                    ? "Accepted: the request's initial resource, scheduled. Its erasure runs as a background job once its " + WindowHours + "-hour cancellable window ends; read the group's requests for its status."
                    : "Accepted: the request's initial resource. Its export runs as a background job; read the group's requests for its status.",
                Shape.Ticket => "The opaque ticket, returned exactly once and never replayed, with the expiry and the chunks it authorizes.",
                Shape.Chunk => "One decrypted chunk: index, count and the base64 of at most " + ChunkSize + " of plaintext. " + BundleShape,
                Shape.Mutation => "Committed receipt, or the retained receipt of a completed exact-key replay.",
                _ => discovery ? "The subject's retained groups on the real host, in order." : "Current receipt-safe projection: status and counts, never content.",
            },
            Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = Ref(document, contract.Response) } },
        };
        var methods = string.Join(", ", path.Operations!.Keys.Select(method => method.Method).Order(StringComparer.Ordinal));
        PublicReadDocumentTransformer.DocumentResponses(document, operation, contract.Method,
        [
            ("400", "validation_failed for malformed or noncanonical input, a missing or extra group, extra or repeated query, an unexpected body, a tenant or organization selector header, an invalid key or precondition, or a key or precondition on an operation that takes none."),
            ("401", "unauthorized for present invalid, expired or undecidable credentials, a missing or invalid native session source, or a binding that is not Active; bare Bearer challenge."),
            ("403", "forbidden when the request carries no retained-entry credential a ticket can bind, or its witness closed."),
            ("404", "not_found for a group the subject does not retain on this host, a request about another person or of another group, and for every ticket or chunk refusal alike."
                + (contract.Shape is Shape.Ticket or Shape.Chunk ? DownloadRecovery : string.Empty)),
            ("405", "method_not_allowed after earlier host, credential and scope gates."),
            ("413", "payload_too_large; request bodies are at most 4 KiB."),
            ("429", "rate_limited by the existing visitor/subject admission."),
            ("500", "internal_error without provider, query, identity, ticket or submitted-value diagnostics."),
            ("503", "dependency_unavailable or audit_unavailable; nothing is disclosed or confirmed without its committed MUST audit row."),
        ], methods);
        if (contract.Body is not null && contract.Shape != Shape.Chunk) operation.Responses["409"] = new OpenApiResponse
        {
            Description = contract.Shape switch
            {
                Shape.Create when Erasure(contract) => OwnErasureConflict,
                Shape.Create => "business_rule_violation: errors.$ carries request_already_active (a request of this kind is still active in the group) or blocked_hold; request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.",
                Shape.Ticket => TicketConflict,
                _ when contract.Name.StartsWith("Revoke", StringComparison.Ordinal) => "concurrency_conflict for a valid stale artifact version; business_rule_violation for a status the transition does not admit (errors.artifact carries data_export_unavailable for an artifact already revoked, expired or purged); request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.",
                _ => "concurrency_conflict for a valid stale version; business_rule_violation for a status the transition does not admit; request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.",
            },
            Headers = new Dictionary<string, IOpenApiHeader> { ["Cache-Control"] = new OpenApiHeader { Required = true, Schema = Enum("no-store") } },
            Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = Ref(document, "ProblemDetails") } },
        };
        if (operation.Responses["503"] is OpenApiResponse unavailable) unavailable.Headers?.Remove("Retry-After");
        if (operation.Responses[status] is not OpenApiResponse success) return;
        success.Headers ??= new Dictionary<string, IOpenApiHeader>();
        if (contract.Shape is Shape.Create or Shape.Mutation)
        {
            success.Headers["ETag"] = new OpenApiHeader { Required = true, Schema = EntityTag(), Description = "The request's version." };
            success.Headers[IdempotentAttribute.ReplayedHeaderName] = new OpenApiHeader
            {
                Required = false,
                Schema = Enum("true"),
                Description = "Present only after fresh original-request disclosure on a replay.",
            };
        }
    }

    private static string RetainedDescription(Contract contract) => contract.Name switch
    {
        "ListRetainedDataRequestScopes" => "The retained own scope of ADR-0106 § 9: the groups on the real host where the verified subject holds or held a Membership of any status or has a data request. On an organization host only that organization's group. No Active Membership, no ordinary native session and no tenant or organization selector. Audit Off. No request body.",
        "ListRetainedDataRequests" => "The subject's own requests in one retained group, newest first, for a Suspended person or a person whose Membership was revoked. A request whose job died is shown failed. Audit Off. No request body.",
        "CreateRetainedDataExport" => "The subject files an export of their own data in one retained group (ADR-0106 § 3, § 9). Refused when the subject holds no Membership of any status there. Strict empty JSON object. MUST audited.",
        "CreateRetainedDataErasure" => "The subject files an erasure of their own data in one retained group (ADR-0106 § 3, § 9; ADR-0107 § 5): the path of a Suspended person and of one whose Membership was revoked. The request is scheduled and its job runs once the " + WindowHours + "-hour window ends; until then it can be cancelled. Refused when the subject holds no Membership of any status there, while the subject is the tenant's only eligible tenant-wide administrator (tenant-wide group) or while a hold applies. Strict empty JSON object. MUST audited.",
        "CancelRetainedDataRequest" => "The subject cancels their own request in one retained group: a requested export (requested to cancelled), or a scheduled erasure before its execution time (scheduled to cancelled). Strict JSON object with requestId only. MUST audited.",
        "RevokeRetainedDataExport" => "The subject revokes an unexpired available artifact of their own request in one retained group, which deletes its chunks at once; the metadata stays as the receipt (ADR-0107 § 4). If-Match carries the ARTIFACT version: the receipt's artifact.version, not the request's. Strict JSON object with requestId only. MUST audited.",
        "MintRetainedDataExportTicket" => "The subject mints a " + TicketMinutes + "-minute download cursor over their own available, unexpired artifact in one retained group, bound to this retained-entry credential; never honoured on the live path. Strict JSON object with requestId only. Not keyed and never replayed; no If-Match. MUST read-sensitive audited; refused when the row cannot be written.",
        _ => "Reads one decrypted chunk through a ticket presented in the body, never the URL, with the retained-entry credential that minted it. Serving the final chunk consumes the ticket. Every refusal is the same not_found. Not keyed. MUST read-sensitive audited per read; refused when the row cannot be written.",
    };

    private static void Describe(OpenApiDocument document, IOpenApiPathItem path, OpenApiOperation operation, Contract contract)
    {
        operation.OperationId = contract.Name;
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TenantAuthenticationRegistration.Scheme, document)] = [] }];
        operation.Description = Description(contract);
        operation.Parameters = [];
        if (contract.Path.Contains("{id}", StringComparison.Ordinal)) operation.Parameters.Add(new OpenApiParameter
        {
            Name = "id",
            In = ParameterLocation.Path,
            Required = true,
            Schema = Uuid(),
            Description = "Canonical lowercase RFC UUIDv7 of a request of the context's group. Another group's request is absent.",
        });
        if (contract.Shape == Shape.Page)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "cursor",
                In = ParameterLocation.Query,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1024 },
                Description = "Opaque canonical current-actor/group/operation-bound cursor. Unknown, repeated, blank and oversized values refuse.",
            });
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "limit",
                In = ParameterLocation.Query,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "2147483647", Default = JsonValue.Create(20) },
                Description = "Canonical positive ASCII decimal Int32, default 20; a page holds at most 100 items and a larger value is clamped to 100 (API Design § Pagination), unlike the retained entry, whose page size is 1 through 20. Signs, leading zeros and repeats refuse. Paging is forward only: follow pageInfo.nextCursor; there is no previous page.",
            });
        }
        if (contract.Body is not null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = Ref(document, contract.Body) } },
            };
            if (contract.Shape is Shape.Create or Shape.Mutation) operation.Parameters.Add(new OpenApiParameter
            {
                Name = IdempotentAttribute.HeaderName,
                In = ParameterLocation.Header,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 8, MaxLength = 128, Pattern = "^[!-~]{8,128}$" },
                Description = "Exactly one printable ASCII nonce, scoped to actor, tenant/organization, operation, method/path/query/body and canonical precondition. No automatic mutation retry.",
            });
            if (contract.Shape == Shape.Mutation) operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-Match",
                In = ParameterLocation.Header,
                Required = true,
                Schema = EntityTag(),
                Description = IfMatchDescription(contract),
            });
        }
        else operation.RequestBody = null;

        operation.Responses ??= [];
        var status = contract.Shape == Shape.Create ? "202" : "200";
        foreach (var other in SuccessStatuses.Where(code => code != status)) operation.Responses.Remove(other);
        operation.Responses[status] = new OpenApiResponse
        {
            Description = contract.Shape switch
            {
                Shape.Create => Erasure(contract)
                    ? "Accepted: the request's initial resource, scheduled. Its erasure runs as a background job once its " + WindowHours + "-hour cancellable window ends; read the Location for its status."
                    : "Accepted: the request's initial resource. Its export runs as a background job; read the Location for its status.",
                Shape.Ticket => "The opaque ticket, returned exactly once and never replayed, with the expiry and the chunks it authorizes.",
                Shape.Chunk => "One decrypted chunk: index, count and the base64 of at most " + ChunkSize + " of plaintext. " + BundleShape,
                Shape.Mutation => "Committed receipt, or the retained receipt of a completed exact-key replay.",
                _ => "Current receipt-safe projection: status and counts, never content.",
            },
            Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = Ref(document, contract.Response) } },
        };
        var methods = string.Join(", ", path.Operations!.Keys.Select(method => method.Method).Order(StringComparer.Ordinal));
        PublicReadDocumentTransformer.DocumentResponses(document, operation, contract.Method,
        [
            ("400", "validation_failed for malformed or noncanonical input, extra or repeated query, an unexpected body, an invalid key or precondition, or a key or precondition on a ticket operation."),
            ("401", "unauthorized for present invalid, expired or undecidable credentials; bare Bearer challenge."),
            ("403", contract.Own ? "forbidden when the request carries no live session the ticket can bind."
                : "forbidden without current identity.membership.admin covering the context's group."),
            ("404", contract.Own ? "not_found without an Active Membership in exactly the context's group, for a request about another person or of another group, and for every ticket or chunk refusal alike."
                    + (contract.Shape is Shape.Ticket or Shape.Chunk ? DownloadRecovery : string.Empty)
                : "not_found for noncoverage, for a request or Membership of another group, a closure, or a request another administrator filed (cancel, revoke)."),
            ("405", "method_not_allowed after earlier host, credential and scope gates."),
            ("413", "payload_too_large; request bodies are at most 16 KiB."),
            ("429", "rate_limited by the existing visitor/subject admission."),
            ("500", "internal_error without provider, query, identity, ticket or submitted-value diagnostics."),
            ("503", "dependency_unavailable or audit_unavailable; nothing is disclosed or confirmed without its committed MUST audit row."),
        ], methods);
        if (contract.Body is not null) operation.Responses["409"] = new OpenApiResponse
        {
            Description = contract.Shape switch
            {
                Shape.Create when Erasure(contract) => contract.Own ? OwnErasureConflict : AdministratorErasureConflict,
                Shape.Create when !contract.Own => "business_rule_violation: errors.$ carries request_already_active (a request of this kind is still active in the group), blocked_hold, or subject_export_available (the person holds a readable export artifact in the group, of any origin, which this export would revoke: it can be filed once that artifact is revoked or has expired); request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.",
                Shape.Create => "business_rule_violation: errors.$ carries request_already_active (a request of this kind is still active in the group) or blocked_hold; request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.",
                Shape.Ticket => TicketConflict,
                Shape.Chunk => "Never: a chunk refusal is not_found.",
                _ => "concurrency_conflict for a valid stale version; business_rule_violation for a status the transition does not admit (errors.artifact carries data_export_unavailable for an artifact already revoked, expired or purged); request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.",
            },
            Headers = new Dictionary<string, IOpenApiHeader> { ["Cache-Control"] = new OpenApiHeader { Required = true, Schema = Enum("no-store") } },
            Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = Ref(document, "ProblemDetails") } },
        };
        if (contract.Shape == Shape.Chunk) operation.Responses.Remove("409");
        if (operation.Responses["503"] is OpenApiResponse unavailable) unavailable.Headers?.Remove("Retry-After");
        if (operation.Responses[status] is not OpenApiResponse success) return;
        success.Headers ??= new Dictionary<string, IOpenApiHeader>();
        if (contract.Shape is Shape.Read or Shape.Create or Shape.Mutation) success.Headers["ETag"] = new OpenApiHeader
        {
            Required = true,
            Schema = EntityTag(),
            Description = contract.Name.StartsWith("Revoke", StringComparison.Ordinal)
                ? "The artifact's version after the revocation (artifact.version), not the request's." : "The request's version.",
        };
        if (contract.Shape is Shape.Create or Shape.Mutation) success.Headers[IdempotentAttribute.ReplayedHeaderName] = new OpenApiHeader
        {
            Required = false,
            Schema = Enum("true"),
            Description = "Present only after fresh original-request disclosure on a replay.",
        };
        if (contract.Shape == Shape.Create) success.Headers["Location"] = new OpenApiHeader
        {
            Required = true,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Pattern = "^/api/v1/identity/" + (contract.Own ? "me/data-requests" : "data-requests")
                    + "/[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
            },
            Description = "Exact same-API relative location of the new request's receipt.",
        };
    }

    /// <summary>
    /// The mint's two refusals, told apart (ADR-0107 § 4): gone means file a new export; a bound means wait, or, for the
    /// per-artifact bound, that the artifact's downloads are spent. The numbers are <see cref="DataRequestLimits"/>'s.
    /// </summary>
    private static readonly string ChunkSize = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{DataRequestLimits.ChunkPlaintextBytes / 1024} KiB");

    private static readonly string TicketMinutes = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{DataRequestLimits.TicketLifetime.TotalMinutes}");

    private static readonly string WindowHours = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{DataRequestLimits.CancellationWindow.TotalHours}");

    /// <summary>The creation refusals of an erasure the person files (ADR-0106 § 3, § 7): each a closed code in errors.$.</summary>
    private const string OwnErasureConflict = "business_rule_violation: errors.$ carries request_already_active (an erasure is still active in the group), "
        + "blocked_last_administrator (the person is the tenant's only eligible tenant-wide administrator; another must be appointed first) or "
        + "blocked_hold; request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.";

    /// <summary>The creation refusals of an erasure an administrator files for a Membership: the person's own, and its eligibility.</summary>
    private const string AdministratorErasureConflict = "business_rule_violation: errors.$ carries request_already_active (an erasure is still active in the group), "
        + "blocked_last_administrator (the person is the tenant's only eligible tenant-wide administrator; another must be appointed first), "
        + "blocked_hold, or subject_membership_current (the person still holds an Active or Suspended Membership in the group: revoke it first); "
        + "request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.";

    private static bool Erasure(Contract contract) => contract.Name.Contains("Erasure", StringComparison.Ordinal);

    /// <summary>The precondition a mutation names: a revocation's is its artifact's version, every other one the request's.</summary>
    private static string IfMatchDescription(Contract contract) => contract.Name.StartsWith("Revoke", StringComparison.Ordinal)
        ? "One canonical strong quoted decimal ARTIFACT version: the receipt's artifact.version, not the request's version. Missing, weak, wildcard, list and noncanonical values are 400; a valid stale artifact version is 409."
        : "One canonical strong quoted decimal request version. Missing, weak, wildcard, list and noncanonical values are 400; a valid stale version is 409.";

    private static readonly string TicketConflict = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"business_rule_violation. errors.artifact carries data_export_unavailable when the request has no available, unexpired artifact "
        + $"or the key it was sealed under is no longer in the export key ring: the artifact is gone and only a new export helps. It carries "
        + $"data_export_ticket_limit when a ticket bound is reached: this session already holds {DataRequestLimits.MaximumOpenTicketsPerBinding} "
        + $"unconsumed, unexpired tickets over the artifact (wait until one is consumed by its final chunk or expires, "
        + $"{DataRequestLimits.TicketLifetime.TotalMinutes} minutes after it was minted; a ticket of an earlier, refreshed credential does not "
        + $"count), or {DataRequestLimits.MaximumTicketsPerArtifact} tickets have been minted over the artifact in all, which is final: "
        + $"file a new export.");

    private static string Description(Contract contract) => contract.Shape switch
    {
        Shape.Create when contract.Own && Erasure(contract) => "The subject files an erasure of their own data in the session's group (ADR-0106 § 3; ADR-0107 § 5). Requires an Active Membership in exactly that group; no permission key. The request is scheduled, holds the frozen snapshot of the subject's Memberships of any status in the group, and its job is submitted on the same transaction, due when the " + WindowHours + "-hour cancellable window ends. The job then revokes the subject's export artifacts in the group, removes the Membership's role assignments, clears its profile, leaves it as a Revoked tombstone and revokes the pending invitations the subject issued there; the request then awaits the operator's privileged step. Refused while the subject is the tenant's only eligible tenant-wide administrator (tenant-wide group) or while a hold applies. Strict empty JSON object. MUST audited.",
        Shape.Create when Erasure(contract) => "An administrator holding identity.membership.admin over the group files an erasure for the person who held the named Membership in the group, only once that Membership is Revoked: an Active or Suspended member is revoked first, by the separately audited administration act (ADR-0106 § 3). Never for themselves. The request is scheduled and runs when its " + WindowHours + "-hour window ends; the administrator who filed it may cancel it until then. Status and counts only. Strict JSON object with membershipId only. MUST audited.",
        Shape.Create when contract.Own => "The subject files an export of their own data in the session's group (ADR-0106 § 3). Requires an Active Membership in exactly that group; no permission key. The request holds the frozen snapshot of the subject's Memberships of any status in the group and its job is submitted on the same transaction. When it completes, it revokes the subject's previous available artifact in the group, whoever filed that one. Strict empty JSON object. MUST audited.",
        Shape.Create => "An administrator holding identity.membership.admin over the group files an export for the person who holds or held the named Membership, of any status, in the group; never for themselves. Refused while the person holds a readable artifact in the group, of any origin, because the new export would revoke it. The administrator sees status and counts only and never receives the bundle. Strict JSON object with membershipId only. MUST audited.",
        Shape.Page when contract.Own => "The subject's own requests in the session's group, newest first. A request whose job died is shown failed. Audit Off. No request body.",
        Shape.Page => "The group's export and erasure requests, newest first: status and counts only, never content, never a closure. MUST read-sensitive audited; refused when the row cannot be written. No request body.",
        Shape.Read when contract.Own => "The subject's own request receipt: identity, kind, origin, status, timestamps, the recorded refusal or failure code, the closed retained-reference kinds this step counts with their counts (the list is partial: a kind absent from it is not counted, which is not zero) and the artifact's metadata. No content. Audit Off. No request body.",
        Shape.Read => "One request of the group: status and counts only, never content. MUST read-sensitive audited; refused when the row cannot be written. No request body.",
        Shape.Mutation when contract.Name.StartsWith("Cancel", StringComparison.Ordinal) => contract.Own
            ? "The subject cancels a requested export (requested to cancelled) or a scheduled erasure before its execution time (scheduled to cancelled). A request already fulfilling finishes. Strict empty JSON object. MUST audited."
            : "The administrator who filed a request cancels it: an export while it is still requested, an erasure before its execution time. Strict empty JSON object. MUST audited.",
        Shape.Mutation => contract.Own
            ? "The subject revokes an unexpired available artifact of a request about them, which deletes its chunks at once; the metadata stays as the receipt. Strict empty JSON object. MUST audited."
            : "The administrator who filed an export revokes its unexpired available artifact, which deletes its chunks at once. Strict empty JSON object. MUST audited.",
        Shape.Ticket => "The subject mints a " + TicketMinutes + "-minute download cursor over their own available, unexpired artifact, bound to the credential of this session; an administrator never gets one. Stored only as a digest; not keyed and never replayed; no If-Match. Strict empty JSON object. MUST read-sensitive audited; refused when the row cannot be written.",
        _ => "Reads one decrypted chunk through a ticket presented in the body, never the URL, in the session that minted it. Any index below count may be read while the ticket is unconsumed and unexpired; serving the final chunk (count - 1) consumes it, and a resumed download mints a new ticket. Every refusal is the same not_found. Not keyed. MUST read-sensitive audited per read; refused when the row cannot be written.",
    };

    private static void Schemas(OpenApiDocument document)
    {
        var schemas = document.Components!.Schemas!;
        schemas.TryAdd("ProblemDetails", PublicReadDocumentTransformer.Problem());
        schemas.TryAdd("EmptyAdministrationInput", Object());
        schemas.TryAdd("PageInfo", Object(("nextCursor", Text(true)), ("previousCursor", Text(true)),
            ("hasNext", new OpenApiSchema { Type = JsonSchemaType.Boolean }), ("hasPrevious", new OpenApiSchema { Type = JsonSchemaType.Boolean })));
        schemas.TryAdd("AdministrationVersionReceipt", Object(("version", Version())));
        var kind = Enum([.. DataRequestCodes.Kinds.Codes]);
        var status = Enum([.. DataRequestCodes.Statuses.Codes]);
        var origin = Enum([.. DataRequestCodes.Origins.Codes]);
        schemas["DataRequestAcceptedResponse"] = Object(("id", Uuid()), ("kind", Enum("export")), ("status", Enum("requested")), ("version", Version()));
        schemas["DataErasureAcceptedResponse"] = Object(("id", Uuid()), ("kind", Enum("erasure")), ("status", Enum("scheduled")), ("version", Version()));
        schemas["DataRequestRetainedResponse"] = Object(("kind", Enum([.. DataRequestCodes.RetainedKinds.Codes])),
            ("count", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64", Minimum = "0" }));
        schemas["DataExportTruncationResponse"] = Object(("collection", Text(false, 65)), ("included", Count()), ("total", Count()));
        schemas["DataExportArtifactResponse"] = Object(("id", Uuid()), ("status", Enum([.. DataRequestCodes.ArtifactStatuses.Codes])),
            ("expiresAt", DateTime()), ("purgedAt", DateTime(true)),
            ("byteCount", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64", Minimum = "1", Maximum = DataRequestLimits.MaximumBundleBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            ("chunkCount", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = DataRequestLimits.MaximumChunkCount.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            ("truncations", Array(Ref(document, "DataExportTruncationResponse"), DataRequestLimits.MaximumTruncatedCollections)),
            ("version", Version()), ("readable", new OpenApiSchema { Type = JsonSchemaType.Boolean }));
        (string, IOpenApiSchema)[] receipt =
        [
            ("id", Uuid()), ("kind", kind), ("origin", origin), ("status", status), ("createdAt", DateTime()), ("updatedAt", DateTime(true)),
            ("completedAt", DateTime(true)), ("lastRefusal", Nullable(Enum([.. DataRequestCodes.Refusals.Codes]))),
            ("failureReason", Nullable(Enum([.. DataRequestCodes.FailureReasons.Codes]))),
            ("retained", Array(Ref(document, "DataRequestRetainedResponse"), DataRequestCodes.RetainedKinds.Codes.Length)),
            ("artifact", NullableRef(document, "DataExportArtifactResponse")), ("version", Version()), ("executeAt", DateTime(true)),
        ];
        schemas["OwnDataRequestResponse"] = Object(receipt);
        schemas["OwnDataRequestPage"] = Object(("items", Array(Ref(document, "OwnDataRequestResponse"), 100)), ("pageInfo", Ref(document, "PageInfo")));
        schemas["DataRequestResponse"] = Object([.. receipt, ("requestedByYou", new OpenApiSchema { Type = JsonSchemaType.Boolean }),
            ("membershipIds", Array(Uuid(), DataRequestLimits.MaximumSnapshotMemberships))]);
        schemas["DataRequestPage"] = Object(("items", Array(Ref(document, "DataRequestResponse"), 100)), ("pageInfo", Ref(document, "PageInfo")));
        schemas["DataExportForSubjectInput"] = Object(("membershipId", Uuid()));
        schemas["RetainedDataRequestScopeResponse"] = Object(("tenantId", Uuid()), ("organizationId", Nullable(Uuid())));
        schemas["RetainedDataRequestScopePageResponse"] = Object(("items", Array(Ref(document, "RetainedDataRequestScopeResponse"), 20)),
            ("pageInfo", Ref(document, "PageInfo")));
        schemas["RetainedDataRequestTargetInput"] = Object(("requestId", Uuid()));
        schemas["DataExportTicketResponse"] = Object(("ticket", Ticket()), ("expiresAt", DateTime()),
            ("chunkCount", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = DataRequestLimits.MaximumChunkCount.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            ("byteCount", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64", Minimum = "1", Maximum = DataRequestLimits.MaximumBundleBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        schemas["DataExportChunkInput"] = Object(("ticket", Ticket()),
            ("index", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "0", Maximum = (DataRequestLimits.MaximumChunkCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        schemas["RetainedDataExportChunkInput"] = Object(("requestId", Uuid()), ("ticket", Ticket()),
            ("index", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "0", Maximum = (DataRequestLimits.MaximumChunkCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        schemas["DataExportChunkResponse"] = Object(
            ("index", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "0" }),
            ("count", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1" }),
            ("bytes", new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                ContentEncoding = "base64",
                MinLength = 4,
                MaxLength = 174764,
                Description = "Base64 of one chunk of the decrypted bundle, at most " + ChunkSize + " of plaintext. Concatenating every chunk's decoded bytes in index order yields the bundle: one UTF-8 JSON document (application/json; charset=utf-8) whose top-level member schema_version names its format."
            }));
    }

    private static OpenApiSchema Ticket() => new()
    {
        Type = JsonSchemaType.String,
        MinLength = 43,
        MaxLength = 43,
        Pattern = "^[A-Za-z0-9_-]{43}$",
        Description = "Sensitive opaque download ticket: canonical unpadded base64url of 32 bytes. Body only; never a route, query or log.",
    };

    private static OpenApiSchemaReference Ref(OpenApiDocument document, string name) => new(name, document);
    private static OpenApiSchema NullableRef(OpenApiDocument document, string name) =>
        new() { OneOf = [Ref(document, name), new OpenApiSchema { Type = JsonSchemaType.Null }] };

    private static OpenApiSchema Nullable(OpenApiSchema schema)
    {
        schema.Type |= JsonSchemaType.Null;
        schema.Enum?.Add(null!);
        return schema;
    }

    private static OpenApiSchema Object(params (string Name, IOpenApiSchema Schema)[] fields) => new()
    {
        Type = JsonSchemaType.Object,
        AdditionalPropertiesAllowed = false,
        Required = fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal),
        Properties = fields.ToDictionary(field => field.Name, field => field.Schema, StringComparer.Ordinal),
    };

    private static OpenApiSchema Array(IOpenApiSchema item, int maximum) => new() { Type = JsonSchemaType.Array, Items = item, MaxItems = maximum };
    private static OpenApiSchema Count() => new() { Type = JsonSchemaType.Integer, Format = "int64", Minimum = "0", Maximum = "9007199254740991" };
    private static OpenApiSchema Text(bool nullable = false, int? maximum = null) => new()
    { Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String, MaxLength = maximum };
    private static OpenApiSchema Uuid() => new()
    {
        Type = JsonSchemaType.String,
        Format = "uuid",
        Pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
    };
    private static OpenApiSchema Version() => new()
    {
        Type = JsonSchemaType.String,
        MinLength = 1,
        MaxLength = 19,
        Pattern = "^(0|[1-9][0-9]{0,18})$",
        Description = "Canonical decimal aggregate version from 0 through 9223372036854775807. Never a JSON number.",
    };
    private static OpenApiSchema DateTime(bool nullable = false) => new()
    { Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String, Format = "date-time" };
    private static OpenApiSchema EntityTag() => new()
    { Type = JsonSchemaType.String, MinLength = 3, MaxLength = 21, Pattern = "^\"(0|[1-9][0-9]{0,18})\"$" };
    private static OpenApiSchema Enum(params string[] values) => new()
    { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}

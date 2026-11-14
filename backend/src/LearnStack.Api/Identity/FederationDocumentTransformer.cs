using System.Text.Json.Nodes;
using LearnStack.Api.Authentication;
using LearnStack.Api.PublicReads;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Identity;

/// <summary>
/// The exact ADR-0099 wire surface: the protected federation administration operations (no Idempotency-Key,
/// If-Match on the connection revision) and the anonymous sign-in read. Request bodies are read without MVC binding.
/// </summary>
internal sealed class FederationDocumentTransformer : IOpenApiDocumentTransformer
{
    private sealed record Contract(string Path, HttpMethod Method, string Name, string? Body, bool OptionalIfMatch = false);

    private static readonly Contract[] Contracts =
    [
        new("identity/federation", HttpMethod.Get, "GetFederationConnection", null),
        new("identity/federation/configuration", HttpMethod.Put, "ConfigureFederation", "FederationConfigurationInput", OptionalIfMatch: true),
        new("identity/federation/secret-rotations", HttpMethod.Post, "RotateFederationSecret", "FederationSecretInput"),
        new("identity/federation/display-name", HttpMethod.Put, "RenameFederation", "FederationDisplayNameInput"),
        new("identity/federation/test-starts", HttpMethod.Post, "StartFederationTest", "EmptyAdministrationInput"),
        new("identity/federation/test-results", HttpMethod.Post, "RecordFederationTestResult", "FederationTestResultInput"),
        new("identity/federation/activation", HttpMethod.Post, "ActivateFederation", "EmptyAdministrationInput"),
        new("identity/federation/disablement", HttpMethod.Post, "DisableFederation", "EmptyAdministrationInput"),
        new("identity/federation/enablement", HttpMethod.Post, "EnableFederation", "EmptyAdministrationInput"),
        new("identity/federation/deletion", HttpMethod.Post, "DeleteFederation", "EmptyAdministrationInput"),
        new("identity/federation/resumption", HttpMethod.Post, "ResumeFederationOperation", "EmptyAdministrationInput"),
    ];

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (context.DocumentName != "v1" || !document.Paths.ContainsKey("/api/v1/identity/federation")) return Task.CompletedTask;
        document.Components ??= new();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        Schemas(document);
        foreach (var contract in Contracts)
        {
            if (!document.Paths.TryGetValue("/api/v1/" + contract.Path, out var path)
                || path.Operations?.TryGetValue(contract.Method, out var operation) != true || operation is null) continue;
            Describe(document, path, operation, contract);
        }
        return Task.CompletedTask;
    }

    private static void Describe(OpenApiDocument document, IOpenApiPathItem path, OpenApiOperation operation, Contract contract)
    {
        operation.OperationId = contract.Name;
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TenantAuthenticationRegistration.Scheme, document)] = [] }];
        operation.Parameters = [];
        operation.Description = contract.Body is null
            ? "Current tenant's nonsecret federation connection in an orgless context, under Tenant identity.federation.admin. Audit Off: no personal data. One provider readback under the request deadline; a definitive drift or an expired test window is recorded as a MUST apply outcome. No request body or query. No-store."
            : "ADR-0099 intent-first federation operation under Tenant identity.federation.admin in an orgless context. Strict UTF-8 application/json object, at most 16 KiB; unknown, duplicate and missing properties refuse. No Idempotency-Key: the operation converges through If-Match on the connection revision, the pending operation and a fresh read. Every settled outcome is 200 with its outcome; an uncertain or pending operation is settled later by Resume after its quiet window. A secret is never returned, logged or audited.";
        if (contract.Body is not null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = Ref(document, contract.Body) } },
            };
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-Match",
                In = ParameterLocation.Header,
                Required = !contract.OptionalIfMatch,
                Schema = EntityTag(),
                Description = contract.OptionalIfMatch
                    ? "Absent only to create the tenant's first live connection; otherwise one canonical strong quoted decimal connection revision. A stale revision or an existing live connection is 409."
                    : "One canonical strong quoted decimal connection revision. Missing, weak, wildcard, list and noncanonical values are 400; a valid stale revision is 409.",
            });
        }
        else operation.RequestBody = null;
        operation.Responses ??= [];
        operation.Responses["200"] = new OpenApiResponse
        {
            Description = contract.Body is null ? "Current nonsecret connection detail, or a null connection." : "Settled operation receipt.",
            Content = new Dictionary<string, OpenApiMediaType>
            { ["application/json"] = new() { Schema = Ref(document, contract.Body is null ? "FederationConnectionResponse" : "FederationOperationResponse") } },
            Headers = new Dictionary<string, IOpenApiHeader>(),
        };
        PublicReadDocumentTransformer.DocumentResponses(document, operation, contract.Method,
        [
            ("400", "validation_failed for malformed or noncanonical input, an unexpected body or query, an invalid precondition, or a refused egress address."),
            ("401", "unauthorized for present invalid, expired or undecidable credentials; bare Bearer challenge."),
            ("403", "forbidden without current Tenant identity.federation.admin in an orgless context; feature_disabled when the protocol is not entitled."),
            ("404", "not_found for missing credentials, noncoverage or no live connection."),
            ("405", "method_not_allowed after earlier host, credential and scope gates."),
            ("413", "payload_too_large; federation bodies are at most 16 KiB."),
            ("429", "rate_limited by the existing visitor/subject admission."),
            ("500", "internal_error without provider, query, identity or submitted-value diagnostics."),
            ("503", "dependency_unavailable or audit_unavailable: the provider, egress fetcher or storage could not decide; nothing unconfirmed is reported as applied."),
        ], string.Join(", ", path.Operations!.Keys.Select(method => method.Method).Order(StringComparer.Ordinal)));
        if (operation.Responses["503"] is OpenApiResponse unavailable) unavailable.Headers?.Remove("Retry-After");
        if (contract.Name == "RecordFederationTestResult")
        {
            // ADR-0099 Amendment 6 (S-B): the BFF's federation_test callback is the only caller.
            operation.Description += " The access token must also carry the optional federation.test_record client scope, which only the BFF's federation_test authorization requests; an ordinary sign-in or Studio token is refused with 403 before the body is read.";
            if (operation.Responses["403"] is OpenApiResponse forbidden)
                forbidden.Description = "forbidden without current Tenant identity.federation.admin in an orgless context, or when the access token lacks the federation.test_record scope; feature_disabled when the protocol is not entitled.";
        }
        if (contract.Body is not null) operation.Responses["409"] = new OpenApiResponse
        {
            Description = "concurrency_conflict for a stale revision, a pending operation inside its quiet window or a racing first configuration; business_rule_violation for a lifecycle state the operation does not admit or a changed issuer or entity identifier.",
            Headers = new Dictionary<string, IOpenApiHeader> { ["Cache-Control"] = new OpenApiHeader { Required = true, Schema = Enum("no-store") } },
            Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = Ref(document, "ProblemDetails") } },
        };
        if (contract.Body is null && operation.Responses["200"] is OpenApiResponse success)
            success.Headers!["ETag"] = new OpenApiHeader
            {
                Required = false,
                Schema = EntityTag(),
                Description = "The connection revision, present exactly when a live connection exists.",
            };
    }

    private static void Schemas(OpenApiDocument document)
    {
        var schemas = document.Components!.Schemas!;
        schemas.TryAdd("ProblemDetails", PublicReadDocumentTransformer.Problem());
        schemas.TryAdd("EmptyAdministrationInput", Object());
        var outcome = Enum("applied", "failed", "uncertain", "pending", "drift", "recorded", "expired", "superseded");
        var reason = Nullable(Enum("provider_refused", "drift", "owner_mismatch", "egress_refused", "resubmit_required", "provider_unavailable", "egress_unavailable"));
        schemas["FederationOperationResponse"] = Object(("connectionId", Uuid()), ("version", Version()), ("outcome", outcome),
            ("reason", reason), ("testAttemptId", Uuid(nullable: true)));
        schemas["FederationPendingResponse"] = Object(("kind", Enum("configure", "rotate_secret", "rename", "test_start", "disable", "delete")),
            ("startedAt", DateTime()), ("quietUntil", DateTime()));
        schemas["FederationConnectionBody"] = Object(("connectionId", Uuid()), ("protocol", Enum("oidc", "saml")),
            ("alias", new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^lsf-[0-9a-f]{32}$", MinLength = 36, MaxLength = 36 }),
            ("displayName", Text(false, 80)), ("issuerOrEntityId", Text(false, 2048)),
            ("status", Enum("draft", "test_open", "test_passed", "active", "disabled")),
            ("source", Enum("metadata_url", "discovery_url", "manual")), ("clientId", Text(true, 255)), ("configurationRevision", Version()),
            ("configurationDigest", Digest()), ("desiredDigest", Digest()), ("lastProviderDigest", Nullable(Digest())),
            ("pending", Nullable(Ref(document, "FederationPendingResponse"))), ("testExpiresAt", Nullable(DateTime())), ("version", Version()));
        schemas["FederationConnectionResponse"] = Object(("connection", Nullable(Ref(document, "FederationConnectionBody"))),
            ("provider", Enum("not_read", "matches", "drift", "unavailable")));
        var url = Text(true, 2048);
        schemas["FederationConfigurationInput"] = Object(("protocol", Enum("oidc", "saml")), ("displayName", Text(true, 80)),
            ("source", Enum("metadata_url", "discovery_url", "manual")), ("documentUrl", url), ("issuerOrEntityId", Text(true, 2048)),
            ("authorizationEndpoint", url), ("tokenEndpoint", url), ("jwksEndpoint", url), ("userInfoEndpoint", url), ("singleSignOnEndpoint", url),
            ("signingCertificates", new OpenApiSchema { Type = JsonSchemaType.Array, Items = Text(false, 6144), MaxItems = 2, UniqueItems = true }),
            ("clientId", Text(true, 255)), ("clientAuthentication", Nullable(Enum("client_secret_post", "client_secret_basic"))),
            ("clientSecret", WriteOnlySecret(nullable: true)));
        schemas["FederationSecretInput"] = Object(("clientSecret", WriteOnlySecret(nullable: false)));
        schemas["FederationDisplayNameInput"] = Object(("displayName", Text(false, 80)));
        schemas["FederationTestResultInput"] = Object(("testAttemptId", Uuid()), ("passed", new OpenApiSchema { Type = JsonSchemaType.Boolean }));
        schemas["PublicSignInFederation"] = Object(
            ("alias", new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^lsf-[0-9a-f]{32}$", MinLength = 36, MaxLength = 36 }),
            ("displayName", Text(false, 80)));
        schemas["PublicSignIn"] = Object(("federation", Nullable(Ref(document, "PublicSignInFederation"))));
    }

    private static OpenApiSchema WriteOnlySecret(bool nullable) => new()
    {
        Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String,
        MinLength = 1,
        MaxLength = 1024,
        WriteOnly = true,
        Description = "Write-only client secret, at most 1,024 UTF-8 bytes; never the mask literal or a ${ vault reference. Never returned, logged, audited or persisted.",
    };

    private static OpenApiSchemaReference Ref(OpenApiDocument document, string name) => new(name, document);

    private static OpenApiSchema Nullable(IOpenApiSchema schema) => schema switch
    {
        OpenApiSchema inline => WithNull(inline),
        _ => new OpenApiSchema { OneOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] },
    };

    private static OpenApiSchema WithNull(OpenApiSchema schema)
    {
        schema.Type |= JsonSchemaType.Null;
        if (schema.Enum is { } values) values.Add(null!);
        return schema;
    }

    private static OpenApiSchema Object(params (string Name, IOpenApiSchema Schema)[] fields) => new()
    {
        Type = JsonSchemaType.Object,
        AdditionalPropertiesAllowed = false,
        Required = fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal),
        Properties = fields.ToDictionary(field => field.Name, field => field.Schema, StringComparer.Ordinal),
    };

    private static OpenApiSchema Text(bool nullable = false, int? maximum = null) => new()
    { Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String, MaxLength = maximum };

    private static OpenApiSchema Uuid(bool nullable = false) => new()
    {
        Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String,
        Format = "uuid",
        Pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
    };

    private static OpenApiSchema Version() => new()
    {
        Type = JsonSchemaType.String,
        MinLength = 1,
        MaxLength = 19,
        Pattern = "^(0|[1-9][0-9]{0,18})$",
        Description = "Canonical decimal connection revision from 0 through 9223372036854775807. Never a JSON number.",
    };

    private static OpenApiSchema DateTime() => new() { Type = JsonSchemaType.String, Format = "date-time" };
    private static OpenApiSchema Digest() => new() { Type = JsonSchemaType.String, MinLength = 64, MaxLength = 64, Pattern = "^[0-9a-f]{64}$" };
    private static OpenApiSchema EntityTag() => new()
    { Type = JsonSchemaType.String, MinLength = 3, MaxLength = 21, Pattern = "^\"(0|[1-9][0-9]{0,18})\"$" };
    private static OpenApiSchema Enum(params string[] values) => new()
    { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}

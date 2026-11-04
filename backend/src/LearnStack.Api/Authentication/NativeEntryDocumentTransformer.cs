using System.Text.Json.Nodes;
using LearnStack.Api.PublicReads;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Authentication;

/// <summary>Publishes the exact early global-self branch and its separately credentialed scoped POST.</summary>
internal sealed class NativeEntryDocumentTransformer : IOpenApiDocumentTransformer
{
    internal const string SourceScheme = "NativeSessionSource";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.DocumentName != "v1") return Task.CompletedTask;
        document.Components ??= new();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SourceScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = NativeSessionSourcePolicy.SecretHeader,
            Description = "Separate server-to-server native-session credential. Never expose it to browser code or reuse the public forwarding secret. Bearer identity and trusted real-host provenance are also required.",
        };
        var schemas = document.Components.Schemas;
        schemas.TryAdd("ProblemDetails", PublicReadDocumentTransformer.Problem());
        schemas["IdentitySelfMissing"] = Object(("state", Literal("missing")));
        schemas["IdentitySelfActive"] = Object(("state", Literal("active")), ("authorityId", Uuid()),
            ("subjectKey", Digest()), ("actorId", Uuid()));
        schemas["IdentitySelfState"] = new OpenApiSchema
        {
            OneOf = [new OpenApiSchemaReference("IdentitySelfMissing", document), new OpenApiSchemaReference("IdentitySelfActive", document)],
        };
        schemas["SelfContextItem"] = Object(("tenantId", Uuid()), ("organizationId", Uuid(true)),
            ("tenantName", Text()), ("organizationName", Text(true)), ("entryOrigin", new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" }),
            ("canUseCurrentHost", new OpenApiSchema { Type = JsonSchemaType.Boolean }));
        schemas["SelfContextPage"] = Object(("items", new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            MaxItems = 100,
            Items = new OpenApiSchemaReference("SelfContextItem", document),
        }), ("pageInfo", new OpenApiSchemaReference("PageInfo", document)));
        foreach (var descriptor in GlobalSelfOperation.All)
        {
            var bootstrap = descriptor.Kind == IdentitySelfOperation.Bootstrap;
            var contexts = descriptor.Kind == IdentitySelfOperation.Contexts;
            var operation = new OpenApiOperation
            {
                OperationId = bootstrap ? "BootstrapIdentitySelf" : contexts ? "ListIdentitySelfContexts" : "GetIdentitySelf",
                Extensions = new Dictionary<string, IOpenApiExtension> { ["x-version-introduced"] = new JsonNodeExtension(JsonValue.Create("v1")) },
                Tags = new HashSet<OpenApiTagReference> { new("Identity", document) },
                Description = bootstrap
                    ? "Opt-in tenantless natural-key convergent create. New binding returns 201, existing/racing winner returns 200; unknown COMMIT returns 503 and a deliberate retry resolves the actual stored binding. Real creation alone writes the central MUST event. No membership, scoped session or browser cookie is created. Idempotency-Key and scope/assertion selectors are refused. Empty JSON object only, at most 512 bytes."
                    : contexts ? "Off self-only navigation read. Returns only Active self membership scopes with live tenant/organization labels and configured HTTPS entry origins. No profiles, role grants or arbitrary actor selection. Forward-only subject-bound seek cursor, not authority or a snapshot. No request body or assertion/scope selectors."
                    : "Off self-only binding read. Missing is distinct from Active; Closing/Closed/conflicting bindings return 401 and never look missing. No request body, query or assertion/scope selectors.",
                Parameters = contexts ? ContextParameters() : [],
            };
            SetSecurity(document, operation);
            if (bootstrap) EmptyBody(operation);
            DocumentResponses(document, operation, bootstrap, bootstrap ? "POST" : "GET");
            Success(document, operation, "200", bootstrap ? "IdentitySelfActive" : contexts ? "SelfContextPage" : "IdentitySelfState");
            if (bootstrap)
            {
                Success(document, operation, "201", "IdentitySelfActive");
                operation.Responses!["201"].Headers!["Location"] = Header("/api/v1/identity/self");
            }
            document.Paths[descriptor.Path] = new OpenApiPathItem
            {
                Operations = new Dictionary<HttpMethod, OpenApiOperation> { [bootstrap ? HttpMethod.Post : HttpMethod.Get] = operation },
            };
        }
        if (document.Paths.TryGetValue(TenantSurfaceAdmissionMiddleware.NativeSessionAdmissionPath, out var path)
            && path.Operations?.TryGetValue(HttpMethod.Post, out var admission) == true)
        {
            admission.Description = "Explicit initial selection or scope switch only. Requires a current native source, real host ceiling and live stored-actor membership/lifecycle. The central MUST security event names the admitted Membership and commits before an association witness is returned. This proves API admission, never cookie delivery. Ordinary refresh uses Off self/AccountSite reads and does not invoke this POST. No query or Idempotency-Key; empty JSON object only.";
            SetSecurity(document, admission);
            admission.Parameters =
            [
                new OpenApiParameter { Name = NativeSessionSourcePolicy.TenantHeader, In = ParameterLocation.Header, Required = true, Schema = Uuid(), Description = "Canonical lower-case nonempty tenant UUID candidate, verified against the real host and live membership." },
                new OpenApiParameter { Name = NativeSessionSourcePolicy.OrganizationHeader, In = ParameterLocation.Header, Required = false, Schema = Uuid(), Description = "Optional canonical lower-case nonempty organization UUID candidate. Provider scope and legacy assertions, when present, must agree exactly." },
            ];
            EmptyBody(admission);
            DocumentResponses(document, admission, false, "POST");
            schemas["NativeSessionScopeAdmission"] = Object(("actorId", Uuid()), ("tenantId", Uuid()),
                ("organizationId", Uuid(true)), ("authorityId", Uuid()), ("subjectKey", Digest()));
            Success(document, admission, "200", "NativeSessionScopeAdmission");
        }
        return Task.CompletedTask;
    }

    private static void SetSecurity(OpenApiDocument document, OpenApiOperation operation) => operation.Security =
    [new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference(TenantAuthenticationRegistration.Scheme, document)] = [],
        [new OpenApiSecuritySchemeReference(SourceScheme, document)] = [],
    }];

    private static void EmptyBody(OpenApiOperation operation) => operation.RequestBody = new OpenApiRequestBody
    {
        Required = true,
        Content = new Dictionary<string, OpenApiMediaType>
        {
            ["application/json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false, MaxProperties = 0 } },
        },
    };

    private static List<IOpenApiParameter> ContextParameters() =>
    [
        new OpenApiParameter { Name = "cursor", In = ParameterLocation.Query, Required = false, Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1024 }, Description = "Canonical base64url authority/digest-bound forward seek cursor. Foreign/malformed values are refused; no previous-page cursor." },
        new OpenApiParameter { Name = "limit", In = ParameterLocation.Query, Required = false, Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "2147483647", Default = JsonValue.Create(20) }, Description = "Canonical positive ASCII decimal Int32 (1–2147483647), default 20; accepted values above 100 clamp to 100. Leading zeros, repeats, signs, overflow and unknown query keys are refused." },
    ];

    private static void DocumentResponses(OpenApiDocument document, OpenApiOperation operation, bool bootstrap, string allow)
    {
        PublicReadDocumentTransformer.DocumentResponses(document, operation, HttpMethod.Post,
        [
            ("400", "validation_failed for malformed or repeated native selectors/assertions, unexpected query/body or Idempotency-Key."),
            ("401", "unauthorized for invalid/expired native source, undecidable bearer or terminal/conflicting self binding; bare Bearer challenge."),
            ("404", "not_found for missing native source/credentials, unknown/platform host, noncoverage or host/scope disagreement."),
            ("405", "method_not_allowed after earlier host, credential and native-source gates."),
            ("413", "payload_too_large from the existing decoded body bound; outer Kestrel refusals may omit application headers."),
            ("429", "rate_limited from visitor/subject admission; bootstrap can also exhaust the shared authority creation-attempt windows."),
            ("500", "internal_error without provider, query or credential details."),
            ("503", "dependency_unavailable for binding/lifecycle/audit storage, unknown COMMIT, permanent creation capacity or cleanup fault. No association/result payload is disclosed."),
        ], allow);
        if (bootstrap) operation.Responses!["403"] = Problem(document, "forbidden; creation is disabled for a genuinely missing binding. Existing bindings remain readable.");
        // An indeterminate commit/permanent capacity refusal has no promised recovery window.
        ((OpenApiResponse)operation.Responses!["503"]).Headers!.Remove("Retry-After");
    }

    private static OpenApiResponse Problem(OpenApiDocument document, string description) => new()
    {
        Description = description,
        Headers = new Dictionary<string, IOpenApiHeader> { ["Cache-Control"] = Header("no-store") },
        Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ProblemDetails", document) } },
    };
    private static void Success(OpenApiDocument document, OpenApiOperation operation, string status, string schema) => operation.Responses![status] = new OpenApiResponse
    {
        Description = status == "201" ? "New actor binding and central MUST audit committed; no tenant session." : "Successful bounded projection.",
        Headers = new Dictionary<string, IOpenApiHeader> { ["Cache-Control"] = Header("no-store") },
        Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = new OpenApiSchemaReference(schema, document) } },
    };
    private static OpenApiHeader Header(string value) => new() { Required = true, Schema = Literal(value) };
    private static OpenApiSchema Object(params (string Name, IOpenApiSchema Schema)[] fields) => new()
    {
        Type = JsonSchemaType.Object,
        AdditionalPropertiesAllowed = false,
        Required = fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal),
        Properties = fields.ToDictionary(field => field.Name, field => field.Schema, StringComparer.Ordinal),
    };
    private static OpenApiSchema Text(bool nullable = false) => new() { Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String };
    private static OpenApiSchema Uuid(bool nullable = false) { var value = Text(nullable); value.Format = "uuid"; return value; }
    private static OpenApiSchema Digest() => new() { Type = JsonSchemaType.String, MinLength = 64, MaxLength = 64, Pattern = "^[0-9a-f]{64}$" };
    private static OpenApiSchema Literal(string value) => new() { Type = JsonSchemaType.String, Enum = [JsonValue.Create(value)!] };
}

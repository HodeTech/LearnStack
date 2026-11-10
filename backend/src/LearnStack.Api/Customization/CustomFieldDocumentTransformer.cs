using System.Text.Json.Nodes;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Api.PublicReads;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
namespace LearnStack.Api.Customization;

/// <summary>The closed custom-field and own-consent wire surface; no application record serialization is a public contract.</summary>
internal sealed class CustomFieldDocumentTransformer : IOpenApiDocumentTransformer
{
    private static readonly string[] ReflectedImplementationSchemas =
    ["JsonElement", "ConsentScopeResponse", "ConsentWithdrawalResponse", "CustomFieldActiveRevisionResponse", "CustomFieldControlLabels",
        "MembershipCustomFieldControlLabels", "MembershipCustomFieldMetadataResponse", "MembershipCustomFieldResponse"];
    private sealed record Contract(string Path, HttpMethod Method, string Name, string Response, string? Body = null, bool Cas = false, bool Creates = false, bool Native = false);
    private static readonly Contract[] Contracts =
    [
        new("customization/custom-field-definitions",HttpMethod.Get,"ListCustomFieldDefinitions","CustomFieldDefinitionPageResponse"),
        new("customization/custom-field-definitions/{id}",HttpMethod.Get,"GetCustomFieldDefinition","CustomFieldDefinitionResponse"),
        new("customization/custom-field-definitions/{id}/revisions",HttpMethod.Get,"ListCustomFieldDefinitionRevisions","CustomFieldDefinitionPageResponse"),
        new("customization/custom-field-definitions",HttpMethod.Post,"RegisterCustomFieldDefinition","CustomFieldReceiptResponse","CustomFieldDefinitionInput",Creates:true),
        new("customization/custom-field-definitions/{id}",HttpMethod.Put,"ReviseCustomFieldDefinitionDraft","CustomFieldReceiptResponse","CustomFieldDefinitionInput",Cas:true),
        new("customization/custom-field-definitions/{id}/publication",HttpMethod.Post,"PublishCustomFieldDefinition","CustomFieldReceiptResponse","CustomFieldPublicationInput",Cas:true),
        new("customization/custom-field-definitions/{id}/retirement",HttpMethod.Post,"RetireCustomFieldDefinition","CustomFieldReceiptResponse","EmptyAdministrationInput",Cas:true),
        new("customization/custom-field-definitions/{id}/discard",HttpMethod.Post,"DiscardCustomFieldDefinitionDraft","CustomFieldReceiptResponse","EmptyAdministrationInput",Cas:true),
        new("identity/memberships/{id}/custom-fields",HttpMethod.Get,"GetMembershipCustomFields","MembershipCustomFieldsResponse"),
        new("identity/memberships/{id}/custom-fields",HttpMethod.Patch,"SetMembershipCustomFields","CustomFieldReceiptResponse","MembershipCustomFieldPatch",Cas:true),
        new("identity/me/custom-fields",HttpMethod.Get,"GetOwnMembershipCustomFields","MembershipCustomFieldsResponse"),
        new("identity/me/custom-fields",HttpMethod.Patch,"SetOwnMembershipCustomFields","CustomFieldReceiptResponse","MembershipCustomFieldPatch",Cas:true),
        new("tenancy/membership-field-consents/own",HttpMethod.Get,"GetOwnMembershipFieldConsent","OwnFieldConsentCurrentResponse"),
        new("tenancy/membership-field-consents/own/scopes",HttpMethod.Get,"ListOwnFieldConsentScopes","ConsentScopePageResponse",Native:true),
        new("tenancy/membership-field-consents/own/history",HttpMethod.Get,"ListOwnMembershipFieldConsents","ConsentHistoryPageResponse",Native:true),
        new("tenancy/membership-field-consents/grants",HttpMethod.Post,"GrantOwnMembershipFieldConsent","FieldConsentReceiptResponse","FieldConsentGrantInput"),
        new("tenancy/membership-field-consents/withdrawals",HttpMethod.Post,"WithdrawOwnMembershipFieldConsent","FieldConsentReceiptResponse","FieldConsentWithdrawalInput",Native:true),
    ];
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.DocumentName != "v1") return Task.CompletedTask;
        document.Components ??= new(); document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        Schemas(document);
        // These reflected implementation carriers are replaced by the exact wire
        // schemas above. None is a separate public response or additional contract.
        foreach (var name in ReflectedImplementationSchemas) document.Components.Schemas.Remove(name);
        foreach (var c in Contracts)
        {
            if (!document.Paths.TryGetValue("/api/v1/" + c.Path, out var path) || path.Operations?.TryGetValue(c.Method, out var operation) != true || operation is null) continue;
            operation.OperationId = c.Name;
            var security = new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TenantAuthenticationRegistration.Scheme, document)] = [] };
            if (c.Native) security[new OpenApiSecuritySchemeReference(NativeEntryDocumentTransformer.SourceScheme, document)] = [];
            operation.Security = [security]; operation.Parameters = [];
            operation.Description = c.Body is null
                ? "Fresh exact scoped projection. No body; only the declared query keys. No-store. Own consent history remains available through retained scope without an Active Membership; it never authorizes a grant or profile write."
                : "Closed UTF-8 JSON mutation, at most 4 KiB raw and normalized input; duplicate/unknown/missing names refuse. Exact versioned scoped HMAC idempotency; retained receipts contain identifiers and versions only, with fresh original-target MUST disclosure before replay. No automatic retry or exactly-once claim. Missing configured current/retained HMAC material fails only these new mutations closed. Consent uses exact predecessor body CAS, not If-Match; grants require own Active Membership and explicit notice confirmation. Withdrawal remains independent of profile-write or administration permissions.";
            if (c.Path.Contains("{id}", StringComparison.Ordinal)) Add("id", Uuid(), true, ParameterLocation.Path);
            if (c.Body is not null)
            {
                operation.RequestBody = new OpenApiRequestBody { Required = true, Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = Ref(c.Body) } } };
                Add(IdempotentAttribute.HeaderName, new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 8, MaxLength = 128, Pattern = "^[!-~]{8,128}$" }, true, ParameterLocation.Header);
                if (c.Cas) Add("If-Match", new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^\"(0|[1-9][0-9]{0,18})\"$" }, true, ParameterLocation.Header);
            }
            else operation.RequestBody = null;
            if (c.Name == "ListCustomFieldDefinitions") { Add("cursor", Text(false, 256)); Add("status", Literal("Draft", "Active", "Deprecated")); }
            if (c.Name == "ListCustomFieldDefinitionRevisions") Add("cursor", Text(false, 256));
            if (c.Name is "ListOwnFieldConsentScopes" or "ListOwnMembershipFieldConsents")
            { Add("cursor", Text(false, 256)); Add("limit", Integer("1", "20")); }
            if (c.Name is "ListOwnMembershipFieldConsents" or "WithdrawOwnMembershipFieldConsent" or "GetOwnMembershipFieldConsent") Add("membershipId", Uuid(), true);
            if (c.Name == "GetOwnMembershipFieldConsent") { Add("definitionId", Uuid(), true); Add("definitionVersion", Integer("1", "2147483647"), true); }
            operation.Responses ??= [];
            operation.Responses.Remove(c.Creates ? "200" : "201");
            var response = new OpenApiResponse { Description = "Authorized current projection or minimal committed receipt.", Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = Ref(c.Response) } }, Headers = new Dictionary<string, IOpenApiHeader>() };
            operation.Responses[c.Creates ? "201" : "200"] = response;
            if (c.Body is not null && c.Name is not ("GrantOwnMembershipFieldConsent" or "WithdrawOwnMembershipFieldConsent") || c.Name is "GetCustomFieldDefinition" or "GetMembershipCustomFields" or "GetOwnMembershipCustomFields")
                response.Headers["ETag"] = new OpenApiHeader { Required = true, Schema = Text() };
            if (c.Body is not null) response.Headers[IdempotentAttribute.ReplayedHeaderName] = new OpenApiHeader { Required = false, Schema = Literal("true") };
            if (c.Creates) response.Headers["Location"] = new OpenApiHeader { Required = true, Schema = Text(false, 128) };
            PublicReadDocumentTransformer.DocumentResponses(document, operation, c.Method,
            [("400","validation_failed: malformed, duplicate, missing, oversized normalized or noncanonical input."),
             ("401","unauthorized: invalid credentials."),("403","forbidden: no current operation authority."),
             ("404","not_found: foreign, missing or noncovered exact scope/target."),("405","method_not_allowed after earlier admission gates."),
             ("413","payload_too_large: input over the outer byte bound."),("429","rate_limited by existing admission."),
             ("500","internal_error with payload-free diagnostics."),("503","dependency_unavailable or audit_unavailable; no unconfirmed success.")],
                string.Join(", ", path.Operations.Keys.Select(m => m.Method).Order(StringComparer.Ordinal)));
            if (operation.Responses["503"] is OpenApiResponse unavailable) unavailable.Headers?.Remove("Retry-After");
            if (c.Body is not null) operation.Responses["409"] = new OpenApiResponse { Headers = new Dictionary<string, IOpenApiHeader> { ["Cache-Control"] = new OpenApiHeader { Required = true, Schema = Literal("no-store") } }, Description = "concurrency_conflict, business_rule_violation, limit_exceeded, request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable; no automatic retry.", Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = Ref("ProblemDetails") } } };
            void Add(string name, IOpenApiSchema schema, bool required = false, ParameterLocation location = ParameterLocation.Query) => operation.Parameters.Add(new OpenApiParameter { Name = name, In = location, Required = required, Schema = schema });
        }
        // Absence preserves source-release one; adopting v3 always explicitly requests source two.
        if (document.Paths.TryGetValue("/api/v1/identity/permissions", out var permissions) && permissions.Operations?.TryGetValue(HttpMethod.Get, out var registry) == true)
            registry.Parameters!.Add(new OpenApiParameter { Name = "sourceReleaseVersion", In = ParameterLocation.Query, Required = false, Schema = Integer("1", "2"), Description = "Current source release, default1. Source1 returns v2; source2 returns v3; no automatic adoption/grant." });
        return Task.CompletedTask;
        OpenApiSchemaReference Ref(string name) => new(name, document);
    }
    private static void Schemas(OpenApiDocument d)
    {
        var s = d.Components!.Schemas!;
        s["CustomFieldLocalizedText"] = new OpenApiSchema { Type = JsonSchemaType.Object, MaxProperties = 50, AdditionalPropertiesAllowed = true, AdditionalProperties = Text(false, 128) };
        s["CustomFieldNoticeText"] = new OpenApiSchema { Type = JsonSchemaType.Object, MaxProperties = 50, AdditionalPropertiesAllowed = true, AdditionalProperties = Text(false, 512) };
        s["CustomFieldNoticeHashes"] = new OpenApiSchema { Type = JsonSchemaType.Object, MaxProperties = 50, AdditionalPropertiesAllowed = true, AdditionalProperties = Digest() };
        s["CustomFieldFormLabel"] = Object(("location", Text(false, 64)), ("labels", Nullable(Ref("CustomFieldLocalizedText"))), ("choices", Nullable(Array(Ref("CustomFieldLocalizedText"), 20))));
        // The schema document is itself data; finite closed branches enumerate its admitted keywords.
        for (var depth = 2; depth >= 0; depth--)
        {
            var branches = new List<IOpenApiSchema>
            {
                Object(("type",Literal("string")),("minLength",Integer("1","256")),("maxLength",Integer("1","256"))),
                Object(("type",Literal("string")),("minLength",Integer("1","256")),("maxLength",Integer("1","256")),("enum",new OpenApiSchema{Type=JsonSchemaType.Array,Items=Text(false,256),MinItems=1,MaxItems=20,UniqueItems=true})),
                Object(("type",Literal("integer")),("minimum",new OpenApiSchema{Type=JsonSchemaType.Integer,Minimum="-9007199254740991",Maximum="9007199254740991"}),("maximum",new OpenApiSchema{Type=JsonSchemaType.Integer,Minimum="-9007199254740991",Maximum="9007199254740991"})),
                Object(("type",Literal("number")),("minimum",new OpenApiSchema{Type=JsonSchemaType.Number}),("maximum",new OpenApiSchema{Type=JsonSchemaType.Number})),
                Object(("type",Literal("boolean"))),
            };
            if (depth < 2)
            {
                branches.Add(Object(("type", Literal("object")), ("properties", new OpenApiSchema { Type = JsonSchemaType.Object, MinProperties = 1, MaxProperties = 10, AdditionalPropertiesAllowed = true, AdditionalProperties = Ref("CustomFieldSchemaNode" + (depth + 1)) }),
                    ("required", new OpenApiSchema { Type = JsonSchemaType.Array, Items = Key(), MaxItems = 10, UniqueItems = true }), ("additionalProperties", new OpenApiSchema { Type = JsonSchemaType.Boolean, Enum = [JsonValue.Create(false)!] })));
                branches.Add(Object(("type", Literal("array")), ("items", Ref("CustomFieldSchemaNode" + (depth + 1))), ("minItems", Integer("0", "10")), ("maxItems", Integer("1", "10"))));
            }
            s["CustomFieldSchemaNode" + depth] = new OpenApiSchema { OneOf = branches };
            var values = new List<IOpenApiSchema> { Text(false, 256), new OpenApiSchema { Type = JsonSchemaType.Number }, new OpenApiSchema { Type = JsonSchemaType.Boolean } };
            if (depth < 2) { values.Add(Array(Ref("CustomFieldValueNode" + (depth + 1)), 10)); values.Add(new OpenApiSchema { Type = JsonSchemaType.Object, MaxProperties = 10, AdditionalPropertiesAllowed = true, AdditionalProperties = Ref("CustomFieldValueNode" + (depth + 1)) }); }
            s["CustomFieldValueNode" + depth] = new OpenApiSchema { AnyOf = values };
        }
        s["CustomFieldFiniteSchema"] = Object(("$schema", Literal("https://json-schema.org/draft/2020-12/schema")), ("type", Literal("object")), ("properties", Object(("value", Ref("CustomFieldSchemaNode0")))),
            ("required", new OpenApiSchema { Type = JsonSchemaType.Array, Items = Literal("value"), MinItems = 1, MaxItems = 1 }), ("additionalProperties", new OpenApiSchema { Type = JsonSchemaType.Boolean, Enum = [JsonValue.Create(false)!] }));
        var input = Object(("targetEntity", Literal("Membership")), ("key", Key()), ("schemaVersion", Integer("1", "2147483647")), ("schema", Ref("CustomFieldFiniteSchema")), ("labels", Ref("CustomFieldLocalizedText")),
            ("piiCategory", Literal("None", "Identity", "Behaviour", "Sensitive")), ("isRequired", Boolean()), ("sort", Integer("0", "32767")), ("purposeKey", Nullable(Key())), ("notices", Nullable(Ref("CustomFieldNoticeText"))),
            ("formLabels", Nullable(Array(Ref("CustomFieldFormLabel"), 111))));
        s["CustomFieldDefinitionInput"] = input;
        var definition = Object(input.Properties!.Select(p => (p.Key, p.Value)).ToArray());
        foreach (var (name, schema) in new (string, IOpenApiSchema)[] { ("definitionId", Uuid()), ("status", Literal("Draft", "Active", "Deprecated")), ("version", Version()), ("isDiscarded", Boolean()), ("maximumValueBytes", Integer("1", "4096")), ("activeRevision", Nullable(Object(("id", Uuid()), ("version", Version())))) })
        { definition.Properties!.Add(name, schema); definition.Required!.Add(name); }
        s["CustomFieldDefinitionResponse"] = definition;
        s["CustomFieldReceiptResponse"] = Object(("id", Uuid()), ("version", Version()));
        s["FieldConsentReceiptResponse"] = Object(("evidenceId", Uuid()), ("sequence", Version()), ("isGranted", Boolean()));
        s["CustomFieldDefinitionPageResponse"] = Page(Ref("CustomFieldDefinitionResponse"));
        s["CustomFieldPublicationInput"] = Object(("expectedIncumbentId", Nullable(Uuid())), ("expectedIncumbentVersion", Nullable(Version())));
        s["CustomFieldValueEnvelope"] = Object(("value", Ref("CustomFieldValueNode0")));
        var pin = Object(("conceptKey", Key()), ("definitionId", Uuid()), ("schemaVersion", Integer("1", "2147483647")));
        var set = Object(pin.Properties!.Select(p => (p.Key, p.Value)).Concat(new[] { ("envelope", (IOpenApiSchema)Ref("CustomFieldValueEnvelope")), ("consentEvidenceId", (IOpenApiSchema)Nullable(Uuid())) }).ToArray());
        s["MembershipCustomFieldPatch"] = Object(("set", Array(set, 20)), ("remove", Array(pin, 20)));
        var metadata = Object(("schema", Ref("CustomFieldFiniteSchema")), ("labels", Ref("CustomFieldLocalizedText")), ("piiCategory", Literal("None", "Identity", "Behaviour", "Sensitive")), ("sort", Integer("0", "32767")),
            ("purposeKey", Nullable(Key())), ("notices", Nullable(Ref("CustomFieldNoticeText"))), ("formLabels", Nullable(Array(Ref("CustomFieldFormLabel"), 111))), ("noticeHashes", Nullable(Ref("CustomFieldNoticeHashes"))), ("status", Literal("Active", "Deprecated")));
        s["MembershipCustomFieldsResponse"] = Object(("membershipId", Uuid()), ("version", Version()), ("fields", Array(Object(("conceptKey", Key()), ("definitionId", Uuid()), ("schemaVersion", Integer("1", "2147483647")),
            ("state", Literal("Current", "Missing", "Retired", "Withheld")), ("isRequired", Boolean()), ("envelope", Nullable(Ref("CustomFieldValueEnvelope"))), ("definition", metadata), ("consent", Nullable(Ref("FieldConsentReceiptResponse")))), 40)));
        s["FieldConsentGrantInput"] = Object(("definitionId", Uuid()), ("definitionVersion", Integer("1", "2147483647")), ("noticeLocale", Text(false, 64)), ("expectedNoticeHash", Digest()), ("expectedSequence", Integer("0", "9007199254740991")), ("expectedEvidenceId", Nullable(Uuid())), ("confirmed", Boolean()));
        s["FieldConsentWithdrawalInput"] = Object(("evidenceId", Uuid()), ("expectedSequence", Integer("1", "9007199254740991")), ("expectedEvidenceId", Uuid()), ("confirmed", Boolean()));
        s["ConsentDetailsResponse"] = Object(("evidenceId", Uuid()), ("sequence", Version()), ("isGranted", Boolean()), ("membershipId", Uuid()), ("definitionId", Uuid()), ("definitionVersion", Integer("1", "2147483647")),
            ("purposeKey", Key()), ("noticeLocale", Text(false, 64)), ("noticeHash", Digest()), ("noticeText", Text(false, 512)), ("labels", Ref("CustomFieldLocalizedText")));
        s["OwnFieldConsentCurrentResponse"] = Object(("consent", Nullable(Ref("ConsentDetailsResponse"))));
        s["ConsentScopePageResponse"] = Page(Object(("membershipId", Uuid()), ("tenantId", new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }), ("organizationId", Nullable(new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }))));
        s["ConsentHistoryPageResponse"] = Page(Ref("ConsentDetailsResponse"));
        return;
        OpenApiSchemaReference Ref(string name) => new(name, d);
        OpenApiSchema Page(IOpenApiSchema item) => Object(("items", Array(item, 20)), ("pageInfo", Ref("PageInfo")));
    }
    private static OpenApiSchema Object(params (string Name, IOpenApiSchema Schema)[] fields) => new() { Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false, Required = fields.Select(p => p.Name).ToHashSet(StringComparer.Ordinal), Properties = fields.ToDictionary(p => p.Name, p => p.Schema, StringComparer.Ordinal) };
    private static OpenApiSchema Nullable(IOpenApiSchema schema) => new() { AnyOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] };
    private static OpenApiSchema Array(IOpenApiSchema item, int max) => new() { Type = JsonSchemaType.Array, Items = item, MaxItems = max };
    private static OpenApiSchema Text(bool nullable = false, int? max = null) => new() { Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String, MaxLength = max };
    private static OpenApiSchema Boolean() => new() { Type = JsonSchemaType.Boolean };
    private static OpenApiSchema Integer(string min, string max) => new() { Type = JsonSchemaType.Integer, Minimum = min, Maximum = max };
    private static OpenApiSchema Version() => new() { Type = JsonSchemaType.String, Pattern = "^(0|[1-9][0-9]{0,18})$", MaxLength = 19 };
    private static OpenApiSchema Uuid() => new() { Type = JsonSchemaType.String, Format = "uuid", Pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" };
    private static OpenApiSchema Key() => new() { Type = JsonSchemaType.String, Pattern = "^[a-z0-9]+(-[a-z0-9]+)*$", MaxLength = 100 };
    private static OpenApiSchema Digest() => new() { Type = JsonSchemaType.String, Pattern = "^[0-9a-f]{64}$", MinLength = 64, MaxLength = 64 };
    private static OpenApiSchema Literal(params string[] values) => new() { Type = JsonSchemaType.String, Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList() };
}

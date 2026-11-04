using System.Text.Json.Nodes;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Api.PublicReads;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Api.Identity;

/// <summary>Exact served wire contracts for the finite administration actions, independent of permissive JSON binding.</summary>
internal sealed class AdministrationDocumentTransformer : IOpenApiDocumentTransformer
{
    private static readonly string[] CapabilityFlagNames =
    ["canReadTenantMembers", "canReadOrganizationMembers", "canWriteMemberProfiles", "canManageMemberships",
        "canReadRoles", "canChangeRoleGrants", "canAdoptRoleRelease", "canWriteOwnProfile", "canManageOrganizations"];

    private sealed record Contract(string Path, HttpMethod Method, string Operation, string? Response,
        string? Body = null, bool Versioned = false, bool Paged = false, bool Creates = false);

    private static readonly Contract[] Contracts =
    [
        new("identity/organization-memberships", HttpMethod.Get, "ListOrganizationMemberships", null, Paged: true),
        new("identity/memberships/{id}", HttpMethod.Get, "GetMembershipDetail", "MembershipDetailResponse", Versioned: true),
        new("identity/memberships/{id}/profile", HttpMethod.Put, "SetMembershipProfile", "AdministrationVersionReceipt", "MembershipProfileInput", true),
        new("identity/memberships/{id}/role-assignments", HttpMethod.Put, "ReplaceMembershipRoleAssignments", "AdministrationVersionReceipt", "MembershipRolesInput", true),
        new("identity/memberships/{id}/status", HttpMethod.Put, "ChangeMembershipStatus", "AdministrationVersionReceipt", "MembershipStatusInput", true),
        new("identity/me/profile", HttpMethod.Get, "GetOwnMembershipProfile", "OwnMembershipProfileResponse", Versioned: true),
        new("identity/me/profile", HttpMethod.Put, "SetOwnMembershipProfile", "AdministrationVersionReceipt", "MembershipProfileInput", true),
        new("identity/me/contexts", HttpMethod.Get, "ListOwnMembershipContexts", "OwnMembershipContextPage", Paged: true),
        new("identity/me/capabilities", HttpMethod.Get, "GetOwnCapabilities", "OwnCapabilitiesDto"),
        new("identity/roles", HttpMethod.Get, "ListRoles", "RoleDirectoryResponse"),
        new("identity/roles/{id}", HttpMethod.Get, "GetRoleDetail", "RoleDetailResponse", Versioned: true),
        new("identity/permissions", HttpMethod.Get, "GetPermissionRegistry", "PermissionRegistryResponse"),
        new("identity/roles/{id}/grants", HttpMethod.Put, "ChangeRoleGrants", "AdministrationVersionReceipt", "RoleGrantsInput", true),
        new("identity/roles/{id}/manifest-adoptions", HttpMethod.Post, "AdoptRoleGrantManifest", "AdministrationVersionReceipt", "RoleManifestInput", true),
        new("tenancy/organizations", HttpMethod.Get, "ListOrganizations", "OrganizationDirectoryResponse", Paged: true),
        new("tenancy/organizations/{id}", HttpMethod.Get, "GetOrganizationDetail", "OrganizationResponse", Versioned: true),
        new("tenancy/organizations", HttpMethod.Post, "CreateManagedOrganization", "AdministrationVersionReceipt", "OrganizationCreateInput", true, Creates: true),
        new("tenancy/organizations/{id}", HttpMethod.Put, "RenameOrganization", "AdministrationVersionReceipt", "OrganizationRenameInput", true),
        new("tenancy/organizations/{id}/archive", HttpMethod.Post, "ArchiveOrganization", "AdministrationVersionReceipt", "EmptyAdministrationInput", true),
        new("tenancy/default-organization", HttpMethod.Put, "SelectDefaultOrganization", "AdministrationVersionReceipt", "DefaultOrganizationInput", true),
    ];

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (context.DocumentName != "v1" || !Contracts.Any(contract => document.Paths.ContainsKey("/api/v1/" + contract.Path)))
            return Task.CompletedTask;
        document.Components ??= new();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        WriteSchemas(document, context.ApplicationServices.GetRequiredService<IOperationPolicyCatalog>());
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
        operation.OperationId = contract.Operation;
        operation.Security = [new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(TenantAuthenticationRegistration.Scheme, document)] = [],
        }];
        operation.Description = contract.Body is null
            ? "Current live scoped read. No request body; only the declared query fields are accepted. Protected configuration and other-person reads commit their MUST read audit before disclosure. Own context/capability/profile navigation is Off. No-store; capability flags are advisory and never authorization."
            : "Exact protected mutation. Strict UTF-8 application/json object, at most 16 KiB; unknown, duplicate and missing properties refuse. Current authority, exact target scope and business predicates are checked independently. A completed exact-key replay performs a fresh MUST original-target disclosure read before returning the retained receipt; it never reruns the mutation. Unknown outcomes do not prove rollback and are never automatically retried.";
        operation.Parameters = [];
        if (contract.Path.Contains("{id}", StringComparison.Ordinal)) operation.Parameters.Add(new OpenApiParameter
        {
            Name = "id",
            In = ParameterLocation.Path,
            Required = true,
            Schema = Uuid(),
            Description = "Canonical lowercase nonreserved RFC UUIDv7. Another scope never supplies target authority.",
        });
        if (contract.Paged) AddPageParameters(operation);
        if (contract.Body is not null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                { ["application/json"] = new() { Schema = new OpenApiSchemaReference(contract.Body, document) } },
            };
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = IdempotentAttribute.HeaderName,
                In = ParameterLocation.Header,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 8, MaxLength = 128, Pattern = "^[!-~]{8,128}$" },
                Description = "Exactly one printable ASCII nonce, scoped to actor, tenant/organization, operation, method/path/query/body and canonical precondition. No automatic mutation retry.",
            });
            if (!contract.Creates) operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-Match",
                In = ParameterLocation.Header,
                Required = true,
                Schema = EntityTag(),
                Description = "One canonical strong quoted decimal aggregate version in [0, Int64.MaxValue]. Missing, weak, wildcard, list, repeated and noncanonical values are 400; valid stale versions are 409. Default selection uses the Tenant version.",
            });
        }
        else operation.RequestBody = null;

        operation.Responses ??= [];
        var status = contract.Creates ? "201" : "200";
        if (contract.Response is not null)
        {
            operation.Responses.Remove(contract.Creates ? "200" : "201");
            operation.Responses[status] = new OpenApiResponse
            {
                Description = contract.Creates ? "Committed creation receipt." : "Successful current projection or retained committed receipt.",
                Content = new Dictionary<string, OpenApiMediaType>
                { ["application/json"] = new() { Schema = new OpenApiSchemaReference(contract.Response, document) } },
            };
        }
        var methods = string.Join(", ", path.Operations!.Keys.Select(method => method.Method).Order(StringComparer.Ordinal));
        PublicReadDocumentTransformer.DocumentResponses(document, operation, contract.Method,
        [
            ("400", "validation_failed for malformed/noncanonical input, extra/repeated query, unexpected body or invalid key/precondition."),
            ("401", "unauthorized for present invalid/expired or undecidable credentials; bare Bearer challenge."),
            ("403", "forbidden for live coverage without current operation or target disclosure capability."),
            ("404", "not_found for missing credentials, noncoverage or absent/foreign targets; tenant_mismatch for trusted scope/assertion disagreement."),
            ("405", "method_not_allowed after earlier host, credential and scope gates."),
            ("413", "payload_too_large; administration bodies are at most 16 KiB, including undeclared/chunked bodies."),
            ("429", "rate_limited by the existing visitor/subject admission."),
            ("500", "internal_error without provider, query, identity or submitted-value diagnostics."),
            ("503", "dependency_unavailable or audit_unavailable; no successful payload is disclosed on unconfirmed storage, cleanup or MUST audit."),
        ], methods);
        if (contract.Body is not null) operation.Responses["409"] = Problem(document,
            "concurrency_conflict for a valid stale version; business_rule_violation for refused lifecycle/last-admin/last-organization rules; request_in_progress, idempotency_key_reuse or idempotency_outcome_unavailable for retained-key state.");
        if (operation.Responses["503"] is OpenApiResponse unavailable) unavailable.Headers!.Remove("Retry-After");
        if (operation.Responses.TryGetValue(status, out var success) && success is OpenApiResponse response)
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            if (contract.Versioned) response.Headers["ETag"] = new OpenApiHeader { Required = true, Schema = EntityTag() };
            if (contract.Body is not null) response.Headers[IdempotentAttribute.ReplayedHeaderName] = new OpenApiHeader
            {
                Required = false,
                Schema = Enum("true"),
                Description = "Present only after fresh original-target disclosure audit on a replay.",
            };
            if (contract.Creates) response.Headers["Location"] = new OpenApiHeader
            {
                Required = true,
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Pattern = "^/api/v1/tenancy/organizations/[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                },
                Description = "Exact same-API relative location of the newly created original Organization.",
            };
        }
    }

    private static void WriteSchemas(OpenApiDocument document, IOperationPolicyCatalog catalog)
    {
        var schemas = document.Components!.Schemas!;
        schemas.TryAdd("ProblemDetails", PublicReadDocumentTransformer.Problem());
        schemas.TryAdd("PageInfo", Object(("nextCursor", Text(true)), ("previousCursor", Text(true)),
            ("hasNext", Boolean()), ("hasPrevious", Boolean())));
        schemas["AdministrationVersionReceipt"] = Object(("version", Version()));
        schemas["MembershipDetailResponse"] = Object(("membershipId", Uuid()), ("userId", Uuid()),
            ("organizationId", Uuid(true)), ("status", Enum("Active", "Suspended", "Revoked")),
            ("displayName", Text(true, 128)), ("preferredLocale", Text(true, 35)), ("roleIds", Array(Uuid(), 8, true)), ("version", Version()));
        schemas["OwnMembershipProfileResponse"] = Object(("displayName", Text(true, 128)), ("preferredLocale", Text(true, 35)), ("version", Version()));
        schemas["AdministrationGrantResponse"] = new OpenApiSchema
        {
            OneOf = catalog.Definitions.Where(registration => registration.Assignable).Select(registration =>
                (IOpenApiSchema)Object(("scope", Enum(registration.Definition.Scope.ToString())), ("key", Enum(registration.Definition.Key)))).ToList(),
        };
        schemas["RoleSummaryResponse"] = Object(("roleId", Uuid()), ("builtInKey", BuiltInRole()), ("capabilityScope", Scope()), ("version", Version()));
        schemas["RoleDirectoryResponse"] = Object(("items", Array(Ref("RoleSummaryResponse"), 8)));
        schemas["RoleDetailResponse"] = Object(("roleId", Uuid()), ("builtInKey", BuiltInRole()), ("capabilityScope", Scope()), ("version", Version()),
            ("grants", Array(Ref("AdministrationGrantResponse"), 8, true)), ("adoptedReleaseVersion", Integer()), ("adoptedReleaseFingerprint", Digest()));
        schemas["PermissionRegistryItemResponse"] = Object(("scope", Scope()), ("key", Text(false, 128)), ("labelKey", Text()), ("canGrant", Boolean()));
        schemas["RoleReleaseResponse"] = Object(("version", Integer()), ("fingerprint", Digest()),
            ("tenantAdminDelta", Array(Ref("AdministrationGrantResponse"), 8, true)));
        schemas["PermissionRegistryResponse"] = Object(("items", Array(Ref("PermissionRegistryItemResponse"), 8)), ("release", Ref("RoleReleaseResponse")));
        schemas["OwnCapabilitiesDto"] = Object(CapabilityFlagNames
            .Select(name => (name, (IOpenApiSchema)Boolean())).ToArray());
        schemas["OwnMembershipContextResponse"] = Object(("tenantId", Uuid()), ("organizationId", Uuid(true)),
            ("tenantName", Text()), ("organizationName", Text(true)), ("entryOrigin", new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" }),
            ("canUseCurrentHost", Boolean()));
        schemas["OwnMembershipContextPage"] = Object(("items", Array(Ref("OwnMembershipContextResponse"), 100)), ("pageInfo", Ref("PageInfo")));
        schemas["OrganizationResponse"] = Object(("organizationId", Uuid()), ("slug", Text(false, 63)), ("displayName", Text(false, 200)),
            ("status", Enum("Active", "Suspended", "Archived")), ("isDefault", Boolean()), ("version", Version()));
        schemas["OrganizationDirectoryResponse"] = Object(("items", Array(Ref("OrganizationResponse"), 100)),
            ("defaultOrganizationId", Uuid(true)), ("tenantVersion", Version()), ("pageInfo", Ref("PageInfo")));
        schemas["MembershipProfileInput"] = Object(("displayName", Text(true, 128)), ("preferredLocale", Text(true, 35)));
        schemas["MembershipRolesInput"] = Object(("roleIds", Array(Uuid(), 8, true)));
        schemas["MembershipStatusInput"] = Object(("status", Enum("Active", "Suspended", "Revoked")));
        schemas["RoleGrantsInput"] = Object(("grants", Array(Ref("AdministrationGrantResponse"), 8, true)));
        schemas["RoleManifestInput"] = Object(("manifestVersion", Integer()), ("manifestFingerprint", Digest()));
        schemas["OrganizationCreateInput"] = Object(("slug", Text(false, 63)), ("displayName", Text(false, 200)));
        schemas["OrganizationRenameInput"] = Object(("displayName", Text(false, 200)));
        schemas["EmptyAdministrationInput"] = Object();
        schemas["DefaultOrganizationInput"] = Object(("organizationId", Uuid()));
        return;
        OpenApiSchemaReference Ref(string name) => new(name, document);
    }

    private static void AddPageParameters(OpenApiOperation operation)
    {
        operation.Parameters!.Add(new OpenApiParameter
        {
            Name = "cursor",
            In = ParameterLocation.Query,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1024 },
            Description = "Opaque canonical current-actor/scope/operation-bound cursor. Unknown, repeated, blank and oversized values refuse."
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "limit",
            In = ParameterLocation.Query,
            Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "2147483647", Default = JsonValue.Create(20) },
            Description = "Canonical positive ASCII decimal Int32, default 20, accepted values above 100 clamp to 100. Signs, leading zeros and repeats refuse."
        });
    }

    private static OpenApiResponse Problem(OpenApiDocument document, string description) => new()
    {
        Description = description,
        Headers = new Dictionary<string, IOpenApiHeader> { ["Cache-Control"] = new OpenApiHeader { Required = true, Schema = Enum("no-store") } },
        Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ProblemDetails", document) } },
    };
    private static OpenApiSchema Object(params (string Name, IOpenApiSchema Schema)[] fields) => new()
    {
        Type = JsonSchemaType.Object,
        AdditionalPropertiesAllowed = false,
        Required = fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal),
        Properties = fields.ToDictionary(field => field.Name, field => field.Schema, StringComparer.Ordinal),
    };
    private static OpenApiSchema Array(IOpenApiSchema item, int maximum, bool unique = false) => new()
    { Type = JsonSchemaType.Array, Items = item, MaxItems = maximum, UniqueItems = unique };
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
        Description = "Canonical decimal aggregate version from 0 through 9223372036854775807. Never a JSON number.",
    };
    private static OpenApiSchema EntityTag() => new()
    { Type = JsonSchemaType.String, MinLength = 3, MaxLength = 21, Pattern = "^\"(0|[1-9][0-9]{0,18})\"$" };
    private static OpenApiSchema Boolean() => new() { Type = JsonSchemaType.Boolean };
    private static OpenApiSchema Integer() => new() { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "2147483647" };
    private static OpenApiSchema Digest() => new() { Type = JsonSchemaType.String, MinLength = 64, MaxLength = 64, Pattern = "^[0-9a-f]{64}$" };
    private static OpenApiSchema Scope() => Enum("Tenant", "Organization");
    private static OpenApiSchema BuiltInRole() => Enum("TenantAdmin", "OrgAdmin", "Instructor", "OrgInstructor", "Learner", "Guardian", "Editor");
    private static OpenApiSchema Enum(params string[] values) => new() { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}

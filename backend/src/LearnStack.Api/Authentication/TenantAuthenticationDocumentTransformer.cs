using LearnStack.Api.PublicReads;
using LearnStack.SharedKernel.Pagination;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Authentication;

/// <summary>Documents the two closed authenticated reads and their distinct authority/disclosure bounds.</summary>
internal sealed class TenantAuthenticationDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var operation = Get(document, TenantSurfaceAdmissionMiddleware.AccountSitePath);
        var directory = Get(document, TenantSurfaceAdmissionMiddleware.MembershipsPath);
        if (operation is null && directory is null) return Task.CompletedTask;

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        if (document.Components.Schemas.TryGetValue("DirectoryMembershipStatus", out var status) && status is OpenApiSchema statusSchema)
            statusSchema.Type = JsonSchemaType.String;
        document.Components.Schemas.TryAdd("ProblemDetails", PublicReadDocumentTransformer.Problem());
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[TenantAuthenticationRegistration.Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "The configured learnstack realm's signed RS256 access token for learnstack-api, issued to learnstack-web. Authentication grants no membership or application permission."
        };
        if (operation is not null)
        {
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TenantAuthenticationRegistration.Scheme, document)] = [] }];
            operation.Description = "Authenticated host-backed read through the physical read-only pipeline. Returns only the validated actor UUID, resolved tenant/organization UUIDs and existing public site projection. Accepts no query parameters or client-selected identifiers. No response caching or validators.";
            PublicReadDocumentTransformer.DocumentResponses(document, operation, HttpMethod.Get,
            [
                ("400", "validation_failed; no query parameters are accepted, and malformed or repeated assertion headers are refused."),
            ("401", "unauthorized; present invalid or undecidable credentials on a known host. Bare Bearer challenge; no credential or provider details."),
            ("404", "not_found for absent credentials, unknown hosts, membership refusals or missing host-backed authority; tenant_mismatch for authenticated host/claim or header disagreement, without disclosure."),
            ("405", "method_not_allowed from routing after credential and host-backed authority admission; this operation admits GET only. Absent credentials remain masked as 404."),
            ("413", "payload_too_large; the decoded body bound precedes host classification and authentication. Kestrel framing/backstop refusals may occur outside the application response policy and omit these headers/body."),
            ("429", "rate_limited from early visitor/peer admission, validated-subject quota or subject-registry capacity."),
            ("500", "internal_error from unexpected failures; no exception or query details."),
            ("503", "dependency_unavailable from live authorization storage, invalid public configuration or sticky subject-registry cleanup fault; refused audit writes preserve the original refusal.")
            ], allowedMethods: "GET");
        }
        if (directory is not null) DocumentDirectory(document, directory);
        return Task.CompletedTask;
    }

    private static OpenApiOperation? Get(OpenApiDocument document, string path) =>
        document.Paths.TryGetValue(path, out var item) && item.Operations?.TryGetValue(HttpMethod.Get, out var operation) == true
            ? operation : null;

    private static void DocumentDirectory(OpenApiDocument document, OpenApiOperation operation)
    {
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TenantAuthenticationRegistration.Scheme, document)] = [] }];
        operation.Description = "Tenant administrative membership directory requiring current Tenant identity.membership.admin. Rechecks live membership, lifecycle and capability for every page; accepts HostAndClaim or confirmed ClaimAndMembership, including a platform host. Returns only membership/user/organization identifiers, status, contextual displayName and preferredLocale. Active/Suspended undeleted memberships only, ordered by membership UUID. No search, sorting, editing, provider data, grants or global profile. No-store; the MUST ReadSensitive audit row commits before any result is disclosed. This operation is not admitted by the native BFF caller.";
        operation.Parameters =
        [
            new OpenApiParameter
            {
                Name = "cursor", In = ParameterLocation.Query, Required = false,
                Description = "Opaque canonical continuation bound to the actor, tenant, organization and operation; obtain it from pageInfo.nextCursor. Every page reauthorizes.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1024 },
            },
            new OpenApiParameter
            {
                Name = "limit", In = ParameterLocation.Query, Required = false,
                Description = "Positive page size, default 20; values above 100 are clamped to 100. Repeated, empty or unknown query parameters are refused.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "2147483647", Default = JsonValue.Create(CursorPagination.DefaultLimit) },
            },
        ];
        PublicReadDocumentTransformer.DocumentResponses(document, operation, HttpMethod.Get,
        [
            ("400", "validation_failed for noncanonical/replayed/oversized cursors, invalid limit or unknown/repeated/empty query parameters; malformed assertion headers are also refused."),
            ("401", "unauthorized for present invalid or undecidable credentials on known hosts, including existing-binding failure; bare Bearer challenge without provider details."),
            ("403", "forbidden for confirmed coverage without the current Tenant administrative capability; no directory lookup or disclosure."),
            ("404", "not_found for absent credentials, noncoverage or untrusted scope; tenant_mismatch for host/claim or header disagreement. Assertions do not select authority."),
            ("405", "method_not_allowed from routing after earlier credential, resolution and surface gates; GET only."),
            ("413", "payload_too_large from the existing decoded body bound; outer Kestrel framing refusals may omit application policy."),
            ("429", "rate_limited from visitor/subject admission or subject-registry capacity."),
            ("500", "internal_error for unexpected failures, without exception/query details."),
            ("503", "dependency_unavailable for live membership/lifecycle storage, auxiliary admission or sticky subject-registry cleanup fault; audit_unavailable when the mandatory sensitive-read row cannot commit. No membership payload is disclosed."),
        ], allowedMethods: "GET");
    }

}

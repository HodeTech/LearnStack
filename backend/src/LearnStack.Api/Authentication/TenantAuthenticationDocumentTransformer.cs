using LearnStack.Api.PublicReads;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Authentication;

/// <summary>Documents the one authenticated tenant read admitted by ADR-0064.</summary>
internal sealed class TenantAuthenticationDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Paths.TryGetValue(TenantSurfaceAdmissionMiddleware.AccountSitePath, out var path)
            || path.Operations?.TryGetValue(HttpMethod.Get, out var operation) != true
            || operation is null) return Task.CompletedTask;

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas.TryAdd("ProblemDetails", PublicReadDocumentTransformer.Problem());
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[TenantAuthenticationRegistration.Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "The configured learnstack realm's signed RS256 access token for learnstack-api, issued to learnstack-web. Authentication grants no membership or application permission."
        };
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
            ("503", "dependency_unavailable from invalid stored public configuration or a sticky subject-registry cleanup fault.")
        ], allowedMethods: "GET");
        return Task.CompletedTask;
    }
}

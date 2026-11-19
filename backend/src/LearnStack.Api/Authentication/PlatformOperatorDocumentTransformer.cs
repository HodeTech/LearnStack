using LearnStack.Api.PublicReads;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Authentication;

/// <summary>
/// Gives the operator contract (document <c>platform-v1</c>) what a client author needs and the
/// default generator leaves out: a title, a stable <c>servers</c> list, the bearer scheme with the
/// token profile, and every status an operator can meet (ADR-0101 § 4).
/// </summary>
/// <remarks>
/// <c>servers</c> is dropped for the reason the tenant document drops it: it is derived from the
/// caller's <c>Host</c> header, which no client input may decide.
/// </remarks>
internal sealed class PlatformOperatorDocumentTransformer(string documentName) : IOpenApiDocumentTransformer
{
    internal const string Description =
        "Operator-only platform operations. Authenticate with a short-lived access token issued by the "
        + "deployment's operator issuer to the client learnstack-operator after a second factor; the token "
        + "profile and the runbooks are in docs/runbooks/operator-access.md. Authentication grants no "
        + "permission: each operation needs a Platform grant held by LearnStack and evaluated on every act.";

    public async Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Info.Title = "LearnStack Platform Operator API";
        document.Info.Version = documentName;
        document.Info.Description = Description;
        document.Servers?.Clear();

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas["ProblemDetails"] = PublicReadDocumentTransformer.Problem();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[PlatformOperatorAuthentication.Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "A signed RS256 access token from the operator issuer: audience learnstack-platform, "
                + "authorized party learnstack-operator, an accepted acr that shows the second factor, a recent "
                + "auth_time, and a lifetime of at most 15 minutes.",
        };

        foreach (var path in document.Paths.Values)
        {
            if (path.Operations is null)
            {
                continue;
            }

            foreach (var operation in path.Operations.Values)
            {
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(PlatformOperatorAuthentication.Scheme, document)] = [],
                    },
                ];
                DescribeResponses(document, operation);
                if (operation.RequestBody is null)
                {
                    // Declared here and not through `Accepts` on the endpoint: that metadata makes routing
                    // reject any other content type itself, before the handler can answer 415 as a problem.
                    var schema = await context.GetOrCreateSchemaAsync(
                        BodyFor(operation.OperationId), null, cancellationToken);
                    operation.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } },
                    };
                }
            }
        }
    }

    /// <summary>The request body each operation reads, by the name its endpoint declares.</summary>
    private static Type BodyFor(string? operationId) => operationId switch
    {
        "PlatformKillswitchToggle" => typeof(Platform.KillswitchToggleRequest),
        "PlatformTenantProvision" => typeof(Platform.TenantProvisionRequest),
        "PlatformHostMap" => typeof(Platform.HostMapRequest),
        "PlatformRoleInitialize" => typeof(Platform.RoleInitializeRequest),
        "PlatformFirstAdminCreate" or "PlatformAdminRecover" => typeof(Platform.AdministratorRequest),
        _ => throw new InvalidOperationException("An operator operation declares no request body: " + operationId),
    };

    private static void DescribeResponses(OpenApiDocument document, OpenApiOperation operation)
    {
        operation.Responses ??= [];
        foreach (var (status, description) in Statuses)
        {
            // 409 belongs to the acts that can meet a state they refuse; a switch toggle never answers it.
            if (status == "409" && !operation.Responses.ContainsKey("409"))
            {
                continue;
            }

            operation.Responses[status] = new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ProblemDetails", document) },
                },
            };
        }

        foreach (var (status, response) in operation.Responses)
        {
            if (response is not OpenApiResponse concrete)
            {
                continue;
            }

            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
            if (status == "401")
            {
                concrete.Headers["WWW-Authenticate"] = new OpenApiHeader
                {
                    Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    Description = "Always Bearer; a refused credential is never explained in the response.",
                };
            }

            if (status == "503")
            {
                concrete.Headers["Retry-After"] = new OpenApiHeader
                {
                    Required = true,
                    Description = "Suggested retry delay in seconds; the act is safe to repeat.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "0" },
                };
            }
        }
    }

    private static readonly (string Status, string Description)[] Statuses =
    [
        ("400", "validation_failed: the body is malformed, a required field is missing, an identifier is not a UUIDv7, a provider subject is blank or over 255 characters, a template name is not one of the closed set, or the ticket is not 1 to 64 characters of A-Z a-z 0-9 . _ : / # -. A refusal at the gate or by a route check carries no per-field errors."),
        ("401", "unauthorized: the credential is missing or does not meet the token profile. One answer for every cause; the cause is in the operator's signals."),
        ("403", "forbidden: the credential is good and this operator holds no grant for the operation."),
        ("404", "not_found: the killswitch key is not declared, or the tenant the route names does not exist."),
        ("409", "business_rule_violation: the act refuses the state it found; the reason is in errors (an identifier or slug is taken, the host is taken or reserved, the person has not signed in, the Role is not an initialized administrator Role, the tenant has Membership history or an administrator exists, or another change won). A repeat that changes a value under the same identifiers is refused; an exact repeat answers 200."),
        ("413", "payload_too_large: the body is over 4096 bytes."),
        ("415", "unsupported_media_type: the body is not application/json."),
        ("429", "rate_limited: the caller exceeded the per-peer request budget."),
        ("500", "internal_error: an unexpected failure. An act whose final commit's outcome is unknown may have landed; repeat the same request (every act is idempotent on its identifiers) or read the state before changing it."),
        ("503", "audit_unavailable or dependency_unavailable: the audit row could not be written or an ordered lock could not be taken, so nothing changed. Retry after the delay."),
    ];
}

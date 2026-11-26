using System.Text.Json.Nodes;
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
        + "profile and the runbooks are in docs/runbooks/operator-access.md, and the recovery of dead-lettered "
        + "work is explained in docs/runbooks/operator-recovery.md, the federation operator steps in "
        + "docs/runbooks/federation-operator.md, the completion of an erasure in docs/runbooks/data-erasure.md and the "
        + "closure of a person's account in docs/runbooks/account-closure.md. "
        + "Authentication grants no "
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

            foreach (var (method, operation) in path.Operations)
            {
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(PlatformOperatorAuthentication.Scheme, document)] = [],
                    },
                ];
                DescribeResponses(document, operation, method == HttpMethod.Get);
                if (method == HttpMethod.Get)
                {
                    DescribeInspectionParameters(operation);
                }

                // An inspection is a read and carries no body.
                if (operation.RequestBody is null && method != HttpMethod.Get)
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
        "PlatformDeliveryReplay" => typeof(Platform.DeliveryReplayBody),
        "PlatformOutboxReset" => typeof(Platform.OutboxResetBody),
        "PlatformFederationRemove" or "PlatformFederationDisable" => typeof(Platform.FederationActBody),
        "PlatformErasureComplete" or "PlatformClosureBegin" or "PlatformClosureFinalize" => typeof(Platform.DataRequestActBody),
        _ => throw new InvalidOperationException("An operator operation declares no request body: " + operationId),
    };

    /// <summary>
    /// The page parameters of an inspection by their rule and not only by prose: the closed state set, the page-size
    /// range, the ticket's shape and the cursor's length bound.
    /// </summary>
    private static void DescribeInspectionParameters(OpenApiOperation operation)
    {
        // The quarantined-jobs list takes no state and refuses one, so the document does not offer it.
        if (operation.OperationId == "PlatformJobInspect" && operation.Parameters is { } offered)
        {
            foreach (var state in offered.Where(parameter => parameter.Name == "state").ToList())
            {
                offered.Remove(state);
            }
        }

        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is not OpenApiParameter concrete)
            {
                continue;
            }

            switch (concrete.Name)
            {
                case "state" when operation.OperationId == "PlatformDataRequestInspect":
                    var states = Platform.PlatformOperatorEndpoints.DataRequestStates.Select(state => state.Name).ToList();
                    concrete.Description = states[0] + " (default), " + string.Join(", ", states.Skip(1)) + ".";
                    concrete.Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Enum = [.. states.Select(state => (JsonNode)JsonValue.Create(state)!)],
                    };
                    break;
                case "state":
                    concrete.Description = "dead_lettered (default) or terminal_requested.";
                    concrete.Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Enum = [JsonValue.Create("dead_lettered")!, JsonValue.Create("terminal_requested")!],
                    };
                    break;
                case "limit":
                    concrete.Description = "Page size, 1 or more; a value above 100 is clamped to 100. Zero or less is refused.";
                    concrete.Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Integer,
                        Minimum = "1",
                        Default = JsonValue.Create(20),
                    };
                    break;
                case "cursor":
                    concrete.Description = "The nextCursor of the previous page; it belongs to the route and state that issued it. A federation cursor is at most 64 characters.";
                    concrete.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 512 };
                    break;
                case "ticket":
                    concrete.Description = "An optional change reference, recorded on the platform scope's entry row.";
                    concrete.Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        MinLength = 1,
                        MaxLength = 64,
                        Pattern = Platform.TicketPattern.Value,
                    };
                    break;
            }
        }
    }

    private static void DescribeResponses(OpenApiDocument document, OpenApiOperation operation, bool isRead)
    {
        operation.Responses ??= [];
        foreach (var (status, description) in Statuses)
        {
            // A read has no body to refuse and no row to find: 404, 413 and 415 belong to the acts.
            if (isRead && status is "404" or "413" or "415")
            {
                continue;
            }

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
                    Description = "Suggested retry delay in seconds. Repeat after it unless the body names errors.outcome: "
                        + "then the commit may have landed, and the row is read before the act is repeated.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "0" },
                };
            }
        }
    }

    private static readonly (string Status, string Description)[] Statuses =
    [
        ("400", "validation_failed: the body is malformed, a required field is missing, an onboarding identifier is not a UUIDv7, a recovery route names the nil or the platform tenant or a malformed subscription, a provider subject is blank or over 255 characters, a template name is not one of the closed set, or the ticket is not 1 to 64 characters of A-Z a-z 0-9 . _ : / # -. A refusal at the gate carries no per-field errors; a page parameter that is refused names itself on errors.state, errors.limit, errors.cursor or errors.query (a parameter the route does not take). A federation act names only an alias of the form lsf- and 32 lowercase hexadecimal characters, and an erasure completion or a closure Begin or Finalize only a request identifier that is a UUIDv7."),
        ("401", "unauthorized: the credential is missing or does not meet the token profile. One answer for every cause; the cause is in the operator's signals."),
        ("403", "forbidden: the credential is good and this operator holds no grant for the operation."),
        ("404", "not_found: the killswitch key is not declared, the tenant the route names does not exist, a recovery names no such row, a federation disable names an alias no identity-provider instance has, or an erasure completion or a closure Begin or Finalize names no request."),
        ("409", "business_rule_violation: the act refuses the state it found; the reason is in errors (an identifier or slug is taken, the host is taken or reserved, the person has not signed in, the Role is not an initialized administrator Role, the tenant has Membership history or an administrator exists, or another change won). A recovery answers 409 with the reason on errors.fence (the row no longer shows the fence the inspection returned: read it again) or on errors.state (the fence holds and the row is not one the act may take; the rule that refused it is the reason's cause parameter: processed, leased, invalid_envelope, reason_refused, subscription_not_active, type_not_dispatchable or tenant_not_serving). A repeat that changes a value under the same identifiers is refused; an exact repeat answers 200. A federation act answers 409 with the reason on errors.owner (the instance's owner marker is not LearnStack's, or names another tenant than its row), errors.connection (a live connection row still names the broker and it reads or is still enabled: a remove is refused; disable an unreadable one first, and it is then removed even with the live row), errors.class (a disable found a healthy broker with a live row) or errors.provider (the provider definitively refused the change; nothing changed). A repeat of a federation act that had landed answers 200 already_applied and writes no row of its own, and does not say that a remove owes the account review. An erasure completion answers 409 with the reason on errors.kind (the request is not an erasure), errors.origin (a closure's fan-out erasure, which only that closure's finalization completes) or errors.status (the erasure is not awaiting its privileged step; its stored status is the reason's cause parameter), and writes nothing; a repeat of a completion that had landed answers 200 already_completed and writes no second row. A closure Begin answers 409 with the reason on errors.refusal (the closure was refused and the request stays scheduled with the closed code, the reason's cause parameter, as its last_refusal: blocked_window, blocked_hold or blocked_last_administrator), errors.kind (the request is not a closure), errors.status (the closure is completed or cancelled; its stored status is the reason's cause parameter), errors.account (the person's account is already closed), errors.participants (the person holds Memberships in more participant groups, served or not, or more Memberships in one participant tenant, than one closure is admitted for; no code is recorded, the person stays Active, and Phase 11's data operations serve them) or errors.provider (the identity provider's account-removal client is not configured, or would refuse the person's provider identifier, so the account could never be deleted; no code is recorded and the person stays Active: repair the client by its runbook and Begin again), and changes nothing else; a Begin that committed, or found its closure begun, answers 200 begun or resumed, with complete false while a group's fan-out is waiting or failed. A closure Finalize answers 409 with the reason on errors.kind (the request is not a closure), errors.status (the closure was never begun or was cancelled; its stored status is the reason's cause parameter) or errors.groups (a served group's erasure has not reached its privileged step or did not complete during the closure; the reason's served, ready, pending and unserved parameters are closed counts and no group is named: repeat the Begin, let each group's erasure job run, then finalize again), and writes nothing of the closure, except that a groups refusal found after the provider step also carries the closed provider parameter (removed or absent): the person's account is then already deleted at the provider and a repeat finishes the closure; a repeat of a Finalize that had landed answers 200 already_finalized and writes no row of its own."),
        ("413", "payload_too_large: the body is over 4096 bytes."),
        ("415", "unsupported_media_type: the body is not application/json."),
        ("429", "rate_limited: the caller exceeded the per-peer request budget."),
        ("500", "internal_error: an unexpected failure. An act's final commit may have landed: read the state before changing it. An exact repeat of an onboarding act answers 200; a repeat of a recovery that had landed answers 409 on errors.fence, and of an erasure completion 200 already_completed, and of a closure Begin 200 resumed, and of a closure Finalize 200 already_finalized; a Finalize that fails with 500 after its provider step may also follow a deleted account, so repeat it."),
        ("503", "audit_unavailable or dependency_unavailable: the audit row could not be written, a connection or an ordered lock could not be taken, or a read failed, so nothing changed (except as the closure Finalize clause below says). Retry after the delay. A recovery's dependency_unavailable can carry errors.outcome = lockey_outcome_unknown: its commit may have landed, so inspect the row before repeating, because a landed recovery answers a repeat 409 on errors.fence. A federation act's dependency_unavailable carries it when a provider change was sent and its completion could not be recorded or its answer was lost: read the report or the alias before repeating. An erasure completion's dependency_unavailable carries it when its commit was sent and the answer was lost; a repeat is safe and answers 200 already_completed if the commit had landed, and the redaction is idempotent if it had not. A closure Begin answers dependency_unavailable when another act held one of the person's fences past three seconds, and carries errors.outcome when its commit was sent and the answer was lost; a repeat is safe and answers 200 resumed if the move had landed (or the same refusal again if only a refusal's record had). A closure Finalize answers dependency_unavailable when a fence was held past three seconds, and also when a transient database fault, a refused result row or a lost commit answer follows the provider step: then the person's account may already be deleted at the provider while the closure stays fulfilling, and a repeat finishes it, because a missing account is success. It carries errors.outcome when its final commit was sent and the answer was lost (a repeat answers 200 already_finalized if it had landed), and carries errors.provider when the identity provider did not delete the person's account: lockey_provider_refused (the account-removal client is refused or not configured; repair it by its runbook), lockey_provider_unavailable (the provider could not be reached) or lockey_provider_outcome_unknown (the deletion was sent and its answer was lost). On errors.provider the redaction and its intent row committed and the closure stays fulfilling; a repeat resumes, and a missing account is success."),
    ];
}

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Idempotency;

/// <summary>
/// Publishes the <c>Idempotency-Key</c> contract of an <c>[Idempotent]</c>
/// operation into the OpenAPI document, per
/// <see href="../../../../docs/decisions/0037-idempotency-key-contract.md">ADR-0037</see>.
/// </summary>
/// <remarks>
/// Without this the attribute is invisible to the contract: the generated SDK
/// would not know a required header exists, and every call it produced would be
/// answered <b>400</b>. "The first consumer is a one-attribute change" is only
/// true if the attribute carries its own contract, so it does.
/// </remarks>
internal sealed class IdempotencyOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var isIdempotent = context.Description.ActionDescriptor
            .EndpointMetadata.OfType<IdempotentAttribute>().Any();

        if (!isIdempotent)
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotentAttribute.HeaderName,
            In = ParameterLocation.Header,
            Required = true,
            Description =
                $"A client-chosen key of {IdempotentAttribute.MinKeyLength}–"
                + $"{IdempotentAttribute.MaxKeyLength} printable ASCII characters with no "
                + "space. A key belongs to one request: presenting it for a different "
                + "principal, method, path or body is refused with "
                + "`idempotency_key_reuse` rather than replayed.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MinLength = IdempotentAttribute.MinKeyLength,
                MaxLength = IdempotentAttribute.MaxKeyLength,
            },
        });

        // The 409 is one status with three meanings on this operation, and a
        // generated client branches on the code rather than the status — so the
        // description names all three instead of pretending there is one.
        Describe(operation, "400", "The key is absent, malformed, repeated, or out of range.");
        Describe(
            operation,
            "409",
            "`request_in_progress` — an earlier attempt with this key is still running; "
            + "retry with the same key. `idempotency_key_reuse` — this key belongs to a "
            + "different request; use a new one. `idempotency_outcome_unavailable` — the "
            + "operation completed and its response was not retained.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a response the filter can produce before the action runs. An
    /// existing entry is left alone: the operation's own documentation of a
    /// status wins over this generic one.
    /// </summary>
    private static void Describe(OpenApiOperation operation, string status, string description)
    {
        operation.Responses ??= [];

        if (!operation.Responses.ContainsKey(status))
        {
            operation.Responses[status] = new OpenApiResponse { Description = description };
        }
    }
}

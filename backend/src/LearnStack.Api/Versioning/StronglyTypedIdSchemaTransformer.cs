using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Versioning;

/// <summary>
/// Publishes a strongly-typed identifier as the primitive it wraps, per
/// <see href="../../../../docs/decisions/0023-strongly-typed-id-source-generator.md">ADR-0023
/// § Implementation Notes</see>, which leaves the choice of mechanism to this
/// packet.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0023 offers two ways: an assembly-level <c>[VogenDefaults]</c> in
/// <c>LearnStack.SharedKernel</c>, or a schema transformer here. This is the
/// transformer, because the alternative puts an OpenAPI-shaped attribute in the
/// kernel — a layer that must not know the API surface exists, and one every
/// module references.
/// </para>
/// <para>
/// Detection is by <see cref="IStronglyTypedId{TKey}"/> rather than by Vogen's
/// own marker. The wrapper is ours, the generator is a dependency, and the rule
/// should survive replacing it.
/// </para>
/// <para>
/// Without this a <c>UserId</c> reaches a client as
/// <c>{"value": "018f…"}</c> — an object with one property — while the wire
/// actually carries the bare GUID, because Vogen's
/// <c>SystemTextJson</c> converter already flattens it. The document would
/// describe a shape the API never sends, and the generated SDK would be typed
/// against that shape.
/// </para>
/// </remarks>
internal sealed class StronglyTypedIdSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (UnderlyingKeyOf(context.JsonTypeInfo.Type) is not { } key)
        {
            return Task.CompletedTask;
        }

        // The wrapper's own members are not part of the contract — `Value` is
        // an implementation detail of the struct, not a field on the wire.
        schema.Properties?.Clear();
        schema.Required?.Clear();

        (schema.Type, schema.Format) = key switch
        {
            _ when key == typeof(Guid) => (JsonSchemaType.String, "uuid"),
            _ when key == typeof(long) => (JsonSchemaType.Integer, "int64"),
            _ when key == typeof(int) => (JsonSchemaType.Integer, "int32"),
            _ when key == typeof(string) => (JsonSchemaType.String, null),

            // A key type nobody has used yet. Leaving the schema untouched
            // would publish the wrapper's object shape, which is the bug this
            // transformer exists to prevent, so it says "string" — wrong in
            // detail, right in kind, and visible.
            _ => (JsonSchemaType.String, null),
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// The primitive a strongly-typed identifier wraps, or <c>null</c> when the
    /// type is not one.
    /// </summary>
    private static Type? UnderlyingKeyOf(Type type)
    {
        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }

        return null;
    }
}

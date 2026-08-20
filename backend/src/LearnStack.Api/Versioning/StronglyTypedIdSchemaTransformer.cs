using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Mvc.Controllers;
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
/// Measured, not assumed: without this the published schema for a
/// <c>UserId</c> is <c>{}</c> — the empty schema, which means "anything" — not
/// an object with a <c>value</c> property. Vogen's <c>SystemTextJson</c>
/// converter hands the type to the schema generator in a form it declines to
/// describe. The wire is correct either way (a bare GUID), so the failure is
/// entirely in the contract: the generated SDK types the identifier
/// <c>unknown</c>, which is not a wrong shape but the absence of one, and it
/// propagates to every property that references it.
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

        var type = context.JsonTypeInfo.Type;

        // A collection or map of identifiers is transformed in place: this
        // method is invoked once for `IReadOnlyList<UserId>` with `Items` still
        // null, and the element type never gets a call of its own — measured, an
        // `IReadOnlyList<Guid>` beside it arrives with `items` already set, so
        // the omission is specific to a type the schema generator hands to a
        // converter. Left alone the array publishes with no `items` at all,
        // which every generator reads as `unknown[]`, and a map publishes with
        // no `additionalProperties`, which reads as a map admitting no value.
        if (ElementKeyOf(type) is { } element)
        {
            var elementSchema = new OpenApiSchema();
            Describe(elementSchema, element.Key);

            if (element.IsMap)
            {
                schema.AdditionalProperties = elementSchema;
            }
            else
            {
                schema.Items = elementSchema;
            }

            return Task.CompletedTask;
        }

        if (UnderlyingKeyOf(type) is not { } key)
        {
            // A route- or query-bound identifier never arrives as itself.
            // Measured: for `[FromRoute] UserId id`, both
            // `JsonTypeInfo.Type` and `ParameterDescription.Type` are
            // `System.String` — ApiExplorer collapses a parameter bound through
            // Vogen's TypeConverter before any schema transformer runs. The
            // declared CLR type survives on the descriptor, and recovering it
            // there is the only way this rule reaches `GET /{id}`, which is
            // where identifiers will mostly appear.
            if (DeclaredParameterTypeOf(context) is { } declared
                && UnderlyingKeyOf(declared) is { } parameterKey)
            {
                Describe(schema, parameterKey);
            }

            return Task.CompletedTask;
        }

        // The wrapper's own members are not part of the contract — `Value` is
        // an implementation detail of the struct, not a field on the wire.
        schema.Properties?.Clear();
        schema.Required?.Clear();
        Describe(schema, key);

        return Task.CompletedTask;
    }

    /// <summary>Writes the primitive shape a key type travels as.</summary>
    private static void Describe(OpenApiSchema schema, Type key)
    {
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
    }

    /// <summary>
    /// The primitive a strongly-typed identifier wraps, or <c>null</c> when the
    /// type is not one.
    /// </summary>
    /// <remarks>
    /// <see cref="Nullable{T}"/> is unwrapped first, and that is not a detail:
    /// <c>typeof(UserId?).GetInterfaces()</c> is <b>empty</b>, so without the
    /// unwrap an optional id is skipped — and because .NET registers one shared
    /// <c>components.schemas.UserId</c> that the last writer wins, the skipped
    /// occurrence empties the schema every other occurrence references.
    /// Measured: one <c>UserId?</c> property beside a <c>UserId</c> one publishes
    /// <c>"UserId": {}</c>, which the generator types <c>unknown</c> — and
    /// swapping the two declarations flips the result, so it is positional and
    /// invisible.
    /// </remarks>
    private static Type? UnderlyingKeyOf(Type type)
    {
        foreach (var contract in (Nullable.GetUnderlyingType(type) ?? type).GetInterfaces())
        {
            if (contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <summary>
    /// The type a parameter was <b>declared</b> as, before model binding
    /// flattened it. <c>null</c> when this schema is not a parameter's.
    /// </summary>
    private static Type? DeclaredParameterTypeOf(OpenApiSchemaTransformerContext context)
    {
        if (context.ParameterDescription is not { } parameter)
        {
            return null;
        }

        // MVC controllers — what this API uses — carry the ParameterInfo.
        if (parameter.ParameterDescriptor is ControllerParameterDescriptor controller)
        {
            return controller.ParameterInfo.ParameterType;
        }

        // Minimal APIs resolve the declared type directly, so the fallback is
        // not dead: /healthz is one today and module endpoints may be later.
        return parameter.Type;
    }

    /// <summary>
    /// The key a sequence's element — or a map's value — wraps, and which of the
    /// two it is. <c>null</c> when the type is neither, or holds something that
    /// is not an identifier.
    /// </summary>
    private static (Type Key, bool IsMap)? ElementKeyOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        // Maps first: a dictionary is also an IEnumerable of key-value pairs, so
        // the sequence branch would match it and describe the wrong thing.
        foreach (var contract in Interfaces(type))
        {
            if (!contract.IsGenericType)
            {
                continue;
            }

            var definition = contract.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return UnderlyingKeyOf(contract.GetGenericArguments()[1]) is { } value
                    ? (value, true)
                    : null;
            }
        }

        foreach (var contract in Interfaces(type))
        {
            if (contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return UnderlyingKeyOf(contract.GetGenericArguments()[0]) is { } element
                    ? (element, false)
                    : null;
            }
        }

        return null;

        // An array's element type is not reachable through GetInterfaces() in a
        // form that names it, and the type itself may BE the interface.
        static IEnumerable<Type> Interfaces(Type type) =>
            type.IsInterface ? [type, .. type.GetInterfaces()] : type.GetInterfaces();
    }
}

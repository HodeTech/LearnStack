using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.Versioning;

/// <summary>
/// Stamps every operation with the <c>x-version-introduced</c> extension
/// <see href="../../../../docs/decisions/0024-api-versioning-policy.md">ADR-0024
/// § OpenAPI marking</see> requires, and derives <c>deprecated</c> from
/// <see cref="ObsoleteAttribute"/> rather than from a hand-maintained list.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0024's example shows <c>deprecated: false</c> written out. The emitted
/// document will not contain it, and that is correct rather than a gap:
/// Microsoft.OpenApi omits properties at their default, and OpenAPI defines an
/// absent <c>deprecated</c> as <c>false</c>. Setting it here is what makes a
/// future <c>[Obsolete]</c> operation emit <c>deprecated: true</c>; the false
/// case is carried by the specification, not by the bytes.
/// </para>
/// <para>
/// The remaining extensions in ADR-0024's deprecated example —
/// <c>x-sunset</c>, <c>x-successor</c>, <c>x-migration-guide</c> — are not
/// emitted here. They attach to a deprecated operation, and there is no
/// deprecated operation until a second major exists. That is the packet the
/// ADR names for <c>Every_Deprecated_Endpoint_Has_Sunset_And_Successor</c>;
/// emitting empty extensions now would put three keys in the contract that no
/// consumer can rely on.
/// </para>
/// </remarks>
internal sealed class VersionIntroducedOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
        operation.Extensions["x-version-introduced"] =
            new JsonNodeExtension(JsonValue.Create($"v{ResolveMajor(context)}"));

        operation.Deprecated = context.Description.ActionDescriptor
            .EndpointMetadata.OfType<ObsoleteAttribute>().Any();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the declared major off the controller behind this operation.
    /// Minimal APIs (today only <c>/healthz</c>) have no
    /// <see cref="ControllerActionDescriptor"/> and are unversioned by
    /// ADR-0024; they never reach a versioned document, so the default is only
    /// ever a fallback for a controller that declared nothing.
    /// </summary>
    private static int ResolveMajor(OpenApiOperationTransformerContext context) =>
        context.Description.ActionDescriptor is ControllerActionDescriptor controller
            ? controller.ControllerTypeInfo
                .GetCustomAttributes(typeof(ApiVersionAttribute), inherit: true)
                .OfType<ApiVersionAttribute>()
                .FirstOrDefault()?.Major ?? ApiVersionAttribute.DefaultMajor
            : ApiVersionAttribute.DefaultMajor;
}

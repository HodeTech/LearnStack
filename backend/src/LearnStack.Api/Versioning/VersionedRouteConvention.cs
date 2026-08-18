using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace LearnStack.Api.Versioning;

/// <summary>
/// Prefixes every controller route with <c>api/v{N}</c>, where <c>N</c> comes
/// from <see cref="ApiVersionAttribute"/> (default
/// <see cref="ApiVersionAttribute.DefaultMajor"/>). This is the mechanism
/// <see href="../../../../docs/decisions/0024-api-versioning-policy.md">ADR-0024
/// § Implementation Notes</see> names: "Phase 02a Packet 4 wires the URL
/// convention via ASP.NET Core route conventions".
/// </summary>
/// <remarks>
/// <para>
/// The prefix is applied here rather than written into each controller's
/// <c>[Route]</c> template so that the version lives in exactly one place per
/// controller — the attribute — and a controller cannot drift out of the
/// convention by editing a string. A controller that needs a literal route is
/// still free to write one; it is prefixed, not replaced.
/// </para>
/// <para>
/// Controllers under <see cref="UnversionedRoutePrefixes"/> are left alone.
/// That set is not a convenience: <c>/api/internal/*</c> is the Hub contract
/// surface, which has its own versioning per
/// <see href="../../../../docs/decisions/0019-learnstack-hub.md">ADR-0019</see>
/// and is explicitly outside ADR-0024's scope.
/// </para>
/// </remarks>
public sealed class VersionedRouteConvention : IApplicationModelConvention
{
    /// <summary>
    /// Route prefixes that are exempt from version prefixing. Kept as data so
    /// the convention and the <c>Every_Endpoint_Is_Under_Versioned_Route</c>
    /// architecture test can assert against the same list rather than two
    /// copies that drift.
    /// </summary>
    public static readonly IReadOnlyList<string> UnversionedRoutePrefixes =
    [
        "api/internal",
    ];

    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var controller in application.Controllers)
        {
            if (IsExempt(controller))
            {
                continue;
            }

            var prefix = new AttributeRouteModel(
                new Microsoft.AspNetCore.Mvc.RouteAttribute(
                    $"api/v{ResolveMajor(controller)}"));

            foreach (var selector in controller.Selectors)
            {
                // Idempotent by construction. Registering the convention twice
                // — trivially done by a second AddControllers call in a test
                // host — would otherwise produce `/api/v1/api/v1/...`, which
                // fails as a silent 404 rather than as a startup error.
                if (IsAlreadyVersioned(selector.AttributeRouteModel?.Template))
                {
                    continue;
                }

                // CombineAttributeRouteModel handles the null-left and
                // absolute-right cases; a selector with no route of its own
                // ends up with the bare prefix, which is then completed by the
                // action's own [HttpGet("…")] template.
                selector.AttributeRouteModel =
                    AttributeRouteModel.CombineAttributeRouteModel(
                        prefix,
                        selector.AttributeRouteModel);
            }
        }
    }

    /// <summary>
    /// The major declared by <see cref="ApiVersionAttribute"/>, or
    /// <see cref="ApiVersionAttribute.DefaultMajor"/>. Read from
    /// <see cref="ControllerModel.Attributes"/> rather than by reflecting the
    /// type, because the application model is what MVC actually routes on and
    /// it already carries inherited attributes.
    /// </summary>
    private static int ResolveMajor(ControllerModel controller) =>
        controller.Attributes.OfType<ApiVersionAttribute>().FirstOrDefault()?.Major
            ?? ApiVersionAttribute.DefaultMajor;

    private static bool IsAlreadyVersioned(string? template) =>
        template is not null
        && System.Text.RegularExpressions.Regex.IsMatch(
            template,
            @"^api/v\d+(/|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

    private static bool IsExempt(ControllerModel controller) =>
        controller.Selectors.Any(selector =>
            selector.AttributeRouteModel?.Template is { } template
            && UnversionedRoutePrefixes.Any(prefix =>
                template.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
}

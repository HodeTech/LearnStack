using System.Globalization;
using System.Text.RegularExpressions;
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
/// convention by editing a string.
/// </para>
/// <para>
/// Three things make it fail loudly at startup rather than quietly at runtime:
/// an absolute route template, which MVC would leave outside the prefix; a
/// hand-written <c>api/v{N}</c> prefix that disagrees with the declared
/// attribute; and a major that
/// <see cref="ApiVersioningExtensions.LiveMajors"/> does not carry, which would
/// serve a route no OpenAPI document publishes. Each of those is a 404 or a
/// silently-unversioned endpoint if it is allowed to reach routing.
/// </para>
/// <para>
/// Selectors under <see cref="UnversionedRoutePrefixes"/> are left alone. That
/// set is not a convenience: <c>/api/internal/*</c> is the Hub contract
/// surface, which has its own versioning per
/// <see href="../../../../docs/decisions/0019-learnstack-hub.md">ADR-0019</see>
/// and is explicitly outside ADR-0024's scope.
/// </para>
/// </remarks>
public sealed partial class VersionedRouteConvention : IApplicationModelConvention
{
    private readonly IReadOnlyList<int> _liveMajors;

    /// <summary>
    /// Constructs the convention against a set of live majors, defaulting to
    /// <see cref="ApiVersioningExtensions.LiveMajors"/>.
    /// </summary>
    /// <remarks>
    /// The parameter exists because the live set has to be a seam, not a
    /// compile-time constant: a host that wants a second major — a test
    /// exercising the two-adjacent-majors rule, or a deployment cutting v2 —
    /// must be able to state it, and the convention must then agree with the
    /// documents that were registered. Passing nothing is the production path
    /// and reads the one declared list.
    /// </remarks>
    public VersionedRouteConvention(IReadOnlyList<int>? liveMajors = null) =>
        _liveMajors = liveMajors ?? ApiVersioningExtensions.LiveMajors;

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
            var major = ResolveMajor(controller);
            var name = controller.ControllerType.FullName ?? controller.ControllerName;

            if (!_liveMajors.Contains(major))
            {
                throw new InvalidOperationException(
                    $"'{name}' declares [ApiVersion({major})], which is not in "
                    + $"ApiVersioningExtensions.LiveMajors ("
                    + $"{string.Join(", ", _liveMajors)}). "
                    + "A major that is routable but not published is an endpoint no "
                    + "OpenAPI document describes and no generated SDK can call. Add "
                    + "the major to LiveMajors, or correct the attribute.");
            }

            var prefix = new AttributeRouteModel(
                new Microsoft.AspNetCore.Mvc.RouteAttribute(
                    string.Create(CultureInfo.InvariantCulture, $"api/v{major}")));

            foreach (var selector in controller.Selectors)
            {
                var template = selector.AttributeRouteModel?.Template;

                // Per SELECTOR, not per controller: a controller carrying both
                // an api/internal route and a tenant-facing one must not have
                // the second exempted by the first.
                if (IsExempt(template))
                {
                    continue;
                }

                if (IsAbsolute(template))
                {
                    throw new InvalidOperationException(
                        $"'{name}' declares the absolute route template '{template}'. "
                        + "MVC leaves an absolute template outside any prefix, so the "
                        + "endpoint would be served unversioned — which ADR-0024 "
                        + "forbids. Use a relative template; the convention supplies "
                        + $"the api/v{major} prefix.");
                }

                // Idempotent by construction, and strict about it. Registering
                // the convention twice — trivial in a test host with a second
                // AddControllers — would otherwise yield `/api/v1/api/v1/...`,
                // a silent 404 rather than a startup error. But a hand-written
                // prefix that disagrees with the attribute is a different
                // thing: the route would say one major and
                // VersionIntroducedOperationTransformer, which reads the
                // attribute, would publish another.
                if (AlreadyVersioned().Match(template ?? string.Empty) is { Success: true } match)
                {
                    var written = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
                    if (written != major)
                    {
                        throw new InvalidOperationException(
                            $"'{name}' declares [ApiVersion({major})] but its route "
                            + $"template '{template}' is written under api/v{written}. "
                            + "The attribute is the single authority for the version "
                            + "axis; remove the hand-written prefix.");
                    }

                    continue;
                }

                selector.AttributeRouteModel =
                    AttributeRouteModel.CombineAttributeRouteModel(
                        prefix,
                        selector.AttributeRouteModel);
            }

            // Action selectors carry their own templates ([HttpGet("sub")]),
            // and MVC combines them with the controller's. A relative one is
            // therefore already covered by the prefix applied above — but an
            // absolute one replaces the whole chain, controller prefix
            // included, and would serve an unversioned endpoint from a
            // correctly-versioned controller.
            foreach (var action in controller.Actions)
            {
                foreach (var template in action.Selectors
                             .Select(selector => selector.AttributeRouteModel?.Template)
                             .Where(IsAbsolute))
                {
                    if (IsExempt(template?.TrimStart('~', '/')))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"'{name}.{action.ActionName}' declares the absolute route "
                        + $"template '{template}'. An absolute action template replaces "
                        + "the controller's route entirely, so the endpoint would be "
                        + "served outside api/v{N} — which ADR-0024 forbids. Use a "
                        + "relative template.");
                }
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

    /// <summary>
    /// MVC treats a template starting with <c>/</c> or <c>~/</c> as absolute
    /// and <see cref="AttributeRouteModel.CombineAttributeRouteModel"/> returns
    /// it unchanged, discarding the prefix.
    /// </summary>
    private static bool IsAbsolute(string? template) =>
        template is not null
        && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal));

    private static bool IsExempt(string? template) =>
        template is not null
        && UnversionedRoutePrefixes.Any(prefix =>
            template.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || template.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    // CultureInvariant matters: IgnoreCase alone applies the current culture's
    // casing rules, and under tr-TR 'I' does not lower-case to 'i'. Route
    // shape must not depend on the machine's locale.
    [GeneratedRegex(@"^api/v(?<major>\d+)(/|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlreadyVersioned();
}

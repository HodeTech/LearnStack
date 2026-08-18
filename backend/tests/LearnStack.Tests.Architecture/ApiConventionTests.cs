using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// API-convention rules per
/// <see href="../../../docs/decisions/0024-api-versioning-policy.md">ADR-0024</see>
/// and
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21</see>.
/// The catalogue is the canonical reference for every identifier below.
/// </summary>
public sealed class ApiConventionTests
{
    /// <summary>
    /// Every production assembly that could declare a controller — the API
    /// host plus every module assembly it references. Scanning the whole set
    /// rather than just <c>LearnStack.Api</c> means the rule keeps holding
    /// when a module starts carrying its own controllers.
    /// </summary>
    /// <remarks>
    /// Test assemblies are excluded on purpose: a probe controller in a test
    /// host exists to exercise the convention, and letting it fail this test
    /// would make the rule un-testable. The exclusion is by assembly name, so
    /// it cannot be widened by accident from production code.
    /// </remarks>
    private static readonly Assembly[] ProductionAssemblies = LoadProductionAssemblies();

    private static Assembly[] LoadProductionAssemblies()
    {
        var api = typeof(ApiVersioningExtensions).Assembly;

        return
        [
            api,
            .. api.GetReferencedAssemblies()
                .Where(name => name.Name is { } n
                    && n.StartsWith("LearnStack.", StringComparison.Ordinal)
                    && !n.Contains(".Tests.", StringComparison.Ordinal))
                .Select(Assembly.Load),
        ];
    }

    [Fact]
    public void Every_Endpoint_Is_Under_Versioned_Route()
    {
        // ADR-0024 § Implementation Notes — "no controller action exists
        // outside /api/v{N}/...". The check runs the real
        // VersionedRouteConvention over the real controller set rather than
        // re-deriving the prefix, so a change to the convention that stopped
        // prefixing would fail here instead of quietly agreeing with a second
        // copy of the rule.
        //
        // WHAT THIS TEST DOES NOT PROVE, until the first production controller
        // ships. There are none today, so the assertion below runs over an
        // empty set and passes vacuously. Measured, not assumed: no-op'ing
        // VersionedRouteConvention.Apply leaves all 29 architecture tests
        // green and turns 9 of the 14 ApiVersioningHttpTests red. The runtime
        // tests carry this rule; this one is the net that catches a FUTURE
        // controller declared outside the convention's reach. That asymmetry
        // is why Packet 4 removed the LearnStack.Tests.Integration filter from
        // the backend CI job — with it, breaking the route convention shipped
        // green.
        var model = BuildApplicationModel(ProductionAssemblies);
        new VersionedRouteConvention().Apply(model);

        var unversioned = model.Controllers
            .SelectMany(controller => controller.Selectors
                .Select(selector => new
                {
                    Controller = controller.ControllerType.FullName,
                    Template = selector.AttributeRouteModel?.Template,
                }))
            .Where(route => !IsVersioned(route.Template) && !IsExempt(route.Template))
            .ToList();

        unversioned.Should().BeEmpty(
            "ADR-0024 fixes /api/v{{N}}/ as the only canonical public route shape; "
            + "the unversioned exemptions are the /healthz and /readyz infrastructure "
            + "endpoints (minimal APIs, not controllers) and the /api/internal/* Hub "
            + "surface, which versions itself per ADR-0019");
    }

    [Fact]
    public void Live_Majors_Are_At_Most_Two_Adjacent()
    {
        // ADR-0024 § The version axis — "Two adjacent majors coexist. Three
        // concurrent majors is not supported; cutting /api/v3 requires
        // /api/v1 to have reached its Sunset date first."
        var majors = ApiVersioningExtensions.LiveMajors.OrderBy(major => major).ToList();

        majors.Should().NotBeEmpty();
        majors.Should().OnlyHaveUniqueItems();
        majors.Should().HaveCountLessThanOrEqualTo(2);
        majors[0].Should().BeGreaterThanOrEqualTo(1, "ADR-0024 rules out /api/v0 by name");

        if (majors.Count == 2)
        {
            (majors[1] - majors[0]).Should().Be(1, "the two live majors must be adjacent");
        }
    }

    [Fact]
    public void Unversioned_Route_Prefixes_Are_Declared_Once()
    {
        // The convention and this test read the same list. Asserting its
        // contents keeps a silent widening — someone adding "api" to make a
        // route work — visible as a failing test rather than as a hole.
        VersionedRouteConvention.UnversionedRoutePrefixes
            .Should().Equal("api/internal");
    }

    private static bool IsVersioned(string? template) =>
        template is not null
        && template.StartsWith("api/v", StringComparison.Ordinal)
        && template.Length > "api/v".Length
        && char.IsDigit(template["api/v".Length]);

    private static bool IsExempt(string? template) =>
        template is not null
        && VersionedRouteConvention.UnversionedRoutePrefixes.Any(prefix =>
            template.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds an MVC <see cref="ApplicationModel"/> from the controller types
    /// in <paramref name="assemblies"/>, mirroring what
    /// <c>DefaultApplicationModelProvider</c> produces for the route data this
    /// test inspects.
    /// </summary>
    private static ApplicationModel BuildApplicationModel(IEnumerable<Assembly> assemblies)
    {
        var model = new ApplicationModel();

        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()).Where(IsController))
        {
            var controller = new ControllerModel(
                type.GetTypeInfo(),
                [.. type.GetCustomAttributes(inherit: true).Cast<object>()]);

            foreach (var route in type.GetCustomAttributes<RouteAttribute>(inherit: true))
            {
                controller.Selectors.Add(new SelectorModel
                {
                    AttributeRouteModel = new AttributeRouteModel(route),
                });
            }

            if (controller.Selectors.Count == 0)
            {
                controller.Selectors.Add(new SelectorModel());
            }

            model.Controllers.Add(controller);
        }

        return model;
    }

    private static bool IsController(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsPublic: true }
        && (typeof(ControllerBase).IsAssignableFrom(type)
            || type.Name.EndsWith("Controller", StringComparison.Ordinal));
}

using System.Net;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Versioning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// <c>Every_Endpoint_Is_Under_Versioned_Route</c> and the startup guards that
/// make it hold, per
/// <see href="../../../docs/decisions/0024-api-versioning-policy.md">ADR-0024</see>.
/// </summary>
/// <remarks>
/// These assert against the real <see cref="EndpointDataSource"/> of a host
/// built from <c>Program</c>, so the set under test is the set that is served —
/// not a reflection approximation of it. The earlier reflection form scanned
/// <c>Assembly.GetReferencedAssemblies()</c> and reached four assemblies and no
/// module, while MVC discovers controllers from the runtime dependency graph.
/// </remarks>
public sealed class VersionedRouteEnforcementTests(ProductionHostFixture fixture)
    : IClassFixture<ProductionHostFixture>
{
    [Fact]
    public void Every_Endpoint_Is_Under_Versioned_Route()
    {
        var offenders = fixture.RoutePatterns
            .Where(pattern => !IsVersioned(pattern) && !IsUnversionedByDesign(pattern))
            .ToList();

        offenders.Should().BeEmpty(
            "ADR-0024 fixes /api/v{{N}}/ as the only canonical public route shape. "
            + "The exemptions are the unversioned infrastructure endpoints "
            + "(/healthz, /readyz), the OpenAPI document and its UI, and the "
            + "/api/internal/* Hub surface, which versions itself per ADR-0019");
    }

    [Fact]
    public void The_Endpoint_Set_Is_Not_Empty()
    {
        // Guards the rule above against passing vacuously. A host that failed
        // to discover any endpoint would otherwise report a clean bill of
        // health for a contract it never built.
        fixture.RoutePatterns.Should().NotBeEmpty();
        fixture.RoutePatterns.Should().Contain("healthz");
    }

    [Fact]
    public void The_Production_Host_Sees_No_Test_Controller()
    {
        // The rule above is only meaningful if the fixture's endpoint set is
        // the production one. MVC discovers parts from the application
        // assembly's dependency graph, so a test controller enters only via an
        // explicit AddApplicationPart — which this fixture does not call.
        fixture.RoutePatterns.Should().NotContain(pattern => pattern.Contains("probe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_Absolute_Controller_Route_Fails_At_Startup()
    {
        // The escape the reflection-based test could not see: MVC leaves an
        // absolute template outside every prefix, so the endpoint would be
        // served unversioned. It is refused at startup instead.
        var act = () => StartWithProbeAsync<AbsoluteRouteProbeController>();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*absolute route template*");
    }

    [Fact]
    public async Task An_Absolute_Action_Route_Fails_At_Startup()
    {
        // A correctly-versioned controller can still carry an action whose own
        // template is absolute, which replaces the whole chain.
        var act = () => StartWithProbeAsync<AbsoluteActionRouteProbeController>();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*absolute route template*");
    }

    [Fact]
    public async Task A_Major_Outside_LiveMajors_Fails_At_Startup()
    {
        // Routable but unpublished: no OpenAPI document describes it and no
        // generated SDK can call it.
        var act = () => StartWithProbeAsync<UnpublishedMajorProbeController>();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*LiveMajors*");
    }

    [Fact]
    public async Task A_Hand_Written_Prefix_That_Disagrees_With_The_Attribute_Fails_At_Startup()
    {
        // The idempotency guard skips an already-versioned template. Without
        // this check it doubles as an escape hatch: the route would say one
        // major and the OpenAPI extension, read off the attribute, another.
        var act = () => StartWithProbeAsync<DisagreeingPrefixProbeController>();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*single authority for the version axis*");
    }

    [Fact]
    public async Task A_Bare_ControllerBase_Fails_At_Startup()
    {
        // The worst of the escapes, because it used to fail at REQUEST time:
        // with no controller-level template the convention had nothing to
        // prefix, MVC routed every action at the bare api/v{N} with the
        // resource segment dropped, and a second such controller collided as a
        // 500 AmbiguousMatchException. Without [ApiController] the automatic
        // 400 also never runs, so a malformed body surfaced as a 500
        // internal_error rather than the single Problem Details shape
        // Standards 09 § API Surface fixes. One guard closes both.
        var act = () => StartWithProbeAsync<BareControllerBaseProbeController>();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not carry [ApiController]*");
    }

    [Fact]
    public async Task An_Absolute_Internal_Route_Is_Exempt_At_Both_Levels()
    {
        // The api/internal exemption has to survive normalisation. The
        // action-level guard trimmed '~' and '/' before testing it and the
        // controller-level one did not, so an absolute Hub route was refused
        // at startup — a surface ADR-0024 does not govern at all.
        using var factory = new ProbeHostFixture(typeof(AbsoluteInternalProbeController));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/internal/probe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Matching_Hand_Written_Prefix_Is_Accepted()
    {
        // The guard rejects disagreement, not repetition — the double
        // registration it exists for produces an agreeing prefix.
        using var factory = new ProbeHostFixture(typeof(AgreeingPrefixProbeController));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/agreeing", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task StartWithProbeAsync<TController>()
        where TController : ITestOnlyController
    {
        using var factory = new ProbeHostFixture(typeof(TController));
        using var client = factory.CreateClient();
        await client.GetAsync(new Uri("/healthz", UriKind.Relative));
    }

    private static bool IsVersioned(string pattern) =>
        pattern.StartsWith("api/v", StringComparison.Ordinal)
        && pattern.Length > "api/v".Length
        && char.IsAsciiDigit(pattern["api/v".Length]);

    private static bool IsUnversionedByDesign(string pattern) =>
        pattern is "healthz" or "readyz"
        || pattern.StartsWith("openapi", StringComparison.OrdinalIgnoreCase)
        || pattern.StartsWith("scalar", StringComparison.OrdinalIgnoreCase)
        || VersionedRouteConvention.UnversionedRoutePrefixes.Any(prefix =>
            pattern.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Marks a controller that exists only for a test. Adding the test assembly as
/// an application part brings in every one of them, and several are
/// deliberately broken — so each fixture keeps only the ones it asked for.
/// </summary>
public interface ITestOnlyController;

/// <summary>
/// Removes every <see cref="ITestOnlyController"/> the caller did not ask to
/// keep. Registered at index 0 so it runs before
/// <see cref="VersionedRouteConvention"/>, which would otherwise throw on a
/// probe the fixture never wanted.
/// </summary>
public sealed class TestControllerFilter(params Type[] keep) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var probe in application.Controllers
                     .Where(controller =>
                         typeof(ITestOnlyController).IsAssignableFrom(controller.ControllerType)
                         && !keep.Contains(controller.ControllerType.AsType()))
                     .ToList())
        {
            application.Controllers.Remove(probe);
        }
    }
}

/// <summary>
/// The production host, with no test controllers added — its endpoint set is
/// exactly what a deployed instance serves.
/// </summary>
public sealed class ProductionHostFixture : WebApplicationFactory<Program>
{
    private IReadOnlyList<string>? _patterns;

    public IReadOnlyList<string> RoutePatterns =>
        _patterns ??= [.. Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            // RawText carries a leading '/', the convention's templates do
            // not. Normalise once here so both predicates read the same shape.
            .Select(endpoint => (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .Where(pattern => pattern.Length > 0)];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
    }
}

/// <summary>Host that admits exactly one probe controller, to prove a guard fires.</summary>
public sealed class ProbeHostFixture(Type controller) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(controller)))
                .AddApplicationPart(typeof(ProbeHostFixture).Assembly));
    }
}

[Route("~/escaped")]
public sealed class AbsoluteRouteProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

public sealed class AbsoluteActionRouteProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet("/escaped-action")]
    public IActionResult Get() => Ok();
}

[ApiVersion(9)]
public sealed class UnpublishedMajorProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

[ApiVersion(1)]
[Route("api/v2/disagreeing")]
public sealed class DisagreeingPrefixProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

[ApiVersion(1)]
[Route("api/v1/agreeing")]
public sealed class AgreeingPrefixProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

/// <summary>
/// The exact shape the review reproduced: bare <see cref="ControllerBase"/>,
/// no <c>[ApiController]</c>, no route of its own. Two of these used to route
/// at the same bare <c>api/v1</c> and collide as a 500
/// <c>AmbiguousMatchException</c> at request time.
/// </summary>
public sealed class BareControllerBaseProbeController : ControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

/// <summary>An absolute Hub route: exempt, and must not be refused.</summary>
[ApiController]
[Route("/api/internal/probe")]
public sealed class AbsoluteInternalProbeController : ControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

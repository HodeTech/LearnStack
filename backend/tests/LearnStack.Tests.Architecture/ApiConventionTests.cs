using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Versioning;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The pure-data half of the API-convention rules per
/// <see href="../../../docs/decisions/0024-api-versioning-policy.md">ADR-0024</see>.
/// </summary>
/// <remarks>
/// <c>Every_Endpoint_Is_Under_Versioned_Route</c> deliberately does <b>not</b>
/// live here. It was first written as a reflection scan over
/// <c>Assembly.GetReferencedAssemblies()</c>, which returns the emitted
/// AssemblyRef table rather than the project's references — the compiler elides
/// a reference whose types the IL never touches, so the scan reached four
/// assemblies and no module. MVC discovers controllers from the runtime
/// dependency graph instead, so a module controller was routable and invisible
/// to the test at the same time. The rule now runs against the real
/// <c>EndpointDataSource</c> of a production host in
/// <c>LearnStack.Tests.Integration</c>, where the set under test is the set
/// that is actually served.
/// </remarks>
public sealed class ApiConventionTests
{
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
    public void Forwarded_Headers_Are_Not_Wired()
    {
        // A tripwire, not a prohibition. EffectiveHostAccessor compares the
        // connection peer against the trusted hop's networks, and
        // IHttpConnectionFeature is the same storage UseForwardedHeaders
        // mutates — so the moment that middleware runs ahead of the hop check,
        // the check starts comparing a client-supplied address. The API will
        // want forwarded headers for rate limiting and audit; when it adds
        // them, this test fails and the peer capture has to be moved ahead of
        // them deliberately rather than discovered later.
        typeof(LearnStack.Api.Versioning.ApiVersioningExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .Should().NotContain("Microsoft.AspNetCore.HttpOverrides");

        var program = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepositoryPaths.RepoRoot(),
                "backend", "src", "LearnStack.Api", "Program.cs"));

        program.Should().NotContain("UseForwardedHeaders",
            "see EffectiveHostAccessor.PeerIsInsideTrustedNetwork — the hop's peer "
            + "must be captured before this middleware, or the check compares a "
            + "client-supplied address against the hop's networks");
    }

    [Fact]
    public void Deployment_Mode_Is_Required_Configuration()
    {
        // The file half of the rule. `DeploymentModeConfigurationTests` covers
        // the behaviour — an absent, unknown or ordinal mode refuses to start —
        // and none of those catch the defect this replaces, which was the key
        // being PRESENT: `Deployment:Mode` shipped as "Development" in
        // appsettings.json, the file that goes to every environment, with the
        // same value as the code default. Every Development-guarded mechanism
        // was on by default in a deployment that never configured it, and a
        // guard on the value could not have seen it. Only a guard on the file
        // can.
        var shared = Path.Combine(
            RepositoryPaths.BackendSrc(), "LearnStack.Api", "appsettings.json");

        var document = JsonDocument.Parse(File.ReadAllText(shared));

        // Case-insensitively, because that is how IConfiguration reads it: a
        // lowercase "deployment" block takes effect at runtime exactly the same
        // way and would have walked past a TryGetProperty("Deployment") check.
        document.RootElement.EnumerateObject()
            .Any(property => property.NameEquals("Deployment")
                || string.Equals(property.Name, "Deployment", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse(
                "appsettings.json is loaded in every environment, so a deployment mode set "
                + "there is a default wearing a configuration key's clothes. It belongs in "
                + "appsettings.{Environment}.json or the environment itself.");
    }

    [Fact]
    public void Unversioned_Route_Prefixes_Are_Declared_Once()
    {
        // The convention and the runtime test read the same list. Asserting
        // its contents keeps a silent widening — someone adding "api" to make
        // a route work — visible as a failing test rather than as a hole.
        VersionedRouteConvention.UnversionedRoutePrefixes
            .Should().Equal("api/internal");
    }
}

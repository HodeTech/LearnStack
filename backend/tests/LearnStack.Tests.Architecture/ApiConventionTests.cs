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
    public void Unversioned_Route_Prefixes_Are_Declared_Once()
    {
        // The convention and the runtime test read the same list. Asserting
        // its contents keeps a silent widening — someone adding "api" to make
        // a route work — visible as a failing test rather than as a hole.
        VersionedRouteConvention.UnversionedRoutePrefixes
            .Should().Equal("api/internal");
    }
}

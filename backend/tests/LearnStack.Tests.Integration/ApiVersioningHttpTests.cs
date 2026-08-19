using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Versioning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// HTTP-level coverage for the
/// <see href="../../../docs/decisions/0024-api-versioning-policy.md">ADR-0024</see>
/// surface: the <c>/api/v{N}</c> route convention, one OpenAPI document per
/// live major, the <c>x-version-introduced</c> extension, and the unversioned
/// infrastructure endpoints the ADR allow-lists.
/// </summary>
/// <remarks>
/// These run against the real <c>LearnStack.Api</c> host through
/// <see cref="WebApplicationFactory{TEntryPoint}"/> — no Docker, no database.
/// They are the reason Packet 4 removes the
/// <c>FullyQualifiedName!~LearnStack.Tests.Integration</c> filter from the
/// <c>backend</c> CI job: a route convention is a runtime property, and the
/// structural test alone would pass against a host that never starts.
/// </remarks>
public sealed class ApiVersioningHttpTests(ApiVersioningFixture fixture)
    : IClassFixture<ApiVersioningFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task Controller_Without_An_Explicit_Version_Is_Routed_Under_Api_V1()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/versionprobe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("v1");
    }

    [Fact]
    public async Task Controller_Declaring_A_Major_Is_Routed_Under_That_Major()
    {
        var response = await _client.GetAsync(new Uri("/api/v2/versionprobev2", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unversioned_Route_Is_Not_Reachable()
    {
        // The convention prefixes, it does not add an alias. A controller
        // whose template said "versionprobe" must NOT still answer there, or
        // the contract has two addresses and only one of them is versioned.
        var response = await _client.GetAsync(new Uri("/versionprobe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Healthz_Stays_Unversioned()
    {
        // ADR-0024 § The version axis — /healthz and /readyz are
        // infrastructure endpoints consumed by the orchestrator, not part of
        // the versioned API surface.
        var response = await _client.GetAsync(new Uri("/healthz", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OpenApi_Document_Is_Served_At_The_Address_Standards_04_Publishes()
    {
        var response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("info").GetProperty("version")
            .GetString().Should().Be("v1");
    }

    [Fact]
    public async Task Every_Operation_Carries_x_version_introduced()
    {
        var document = await GetDocumentAsync("v1");
        var paths = document.RootElement.GetProperty("paths");

        paths.EnumerateObject().Should().NotBeEmpty(
            "the probe controller must appear, or this test proves nothing");

        foreach (var path in paths.EnumerateObject())
        {
            // A path item carries non-operation members too — `parameters`,
            // `summary`, `$ref`. Only the HTTP methods are operations.
            foreach (var operation in path.Value.EnumerateObject().Where(IsOperation))
            {
                operation.Value.GetProperty("x-version-introduced")
                    .GetString().Should().Be("v1",
                        "operation {0} {1} must declare the major it was introduced in",
                        operation.Name, path.Name);
            }
        }
    }

    [Fact]
    public async Task Each_Document_Holds_Only_Its_Own_Major()
    {
        // Without the per-document path filter, oasdiff — which Phase 02d
        // wires against the prior main spec — would read a v2 addition as a
        // breaking change to v1.
        var v1 = await GetDocumentAsync("v1");
        var v2 = await GetDocumentAsync("v2");

        v1.RootElement.GetProperty("paths").EnumerateObject()
            .Select(p => p.Name).Should().OnlyContain(p => p.StartsWith("/api/v1/", StringComparison.Ordinal));
        v2.RootElement.GetProperty("paths").EnumerateObject()
            .Select(p => p.Name).Should().OnlyContain(p => p.StartsWith("/api/v2/", StringComparison.Ordinal));

        // Filtering must happen at the ApiDescription level, before schema and
        // tag generation. Removing paths from a finished document leaves the
        // other major's components.schemas and document-level tags behind as
        // orphans — the same mechanism would publish the /api/internal/* Hub
        // surface's shapes in the tenant-facing document.
        TagNames(v1).Should().NotContain("VersionProbeV2");
        TagNames(v2).Should().NotContain("VersionProbe");
    }

    [Fact]
    public async Task Unversioned_Infrastructure_Endpoints_Are_Absent_From_The_Versioned_Document()
    {
        var document = await GetDocumentAsync("v1");

        document.RootElement.GetProperty("paths").EnumerateObject()
            .Select(p => p.Name).Should().NotContain("/healthz");
    }

    [Fact]
    public async Task Scalar_Reference_Ui_Is_Served()
    {
        // Redirects are NOT followed here on purpose. The default
        // WebApplicationFactory client follows them, which would have hidden
        // that /openapi answers 302 -> /openapi/ and reported a green 200 for
        // a URL that does not serve the page.
        using var client = fixture.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var redirect = await client.GetAsync(new Uri("/docs", UriKind.Relative));
        redirect.StatusCode.Should().Be(HttpStatusCode.Found);

        // Location is emitted relative ("docs/"), so resolve it against the
        // request before asserting. Comparing the raw header would pin an
        // implementation detail of Scalar's writer rather than the address.
        redirect.Headers.Location.Should().NotBeNull();
        new Uri(new Uri("http://localhost/docs"), redirect.Headers.Location!)
            .AbsolutePath.Should().Be("/docs/");

        var page = await client.GetAsync(new Uri("/docs/", UriKind.Relative));
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        // The UI must point at the document this packet publishes, not at
        // Scalar's default guess.
        (await page.Content.ReadAsStringAsync()).Should().Contain("openapi/v1.json");
    }

    [Fact]
    public async Task Published_Paths_Are_Lowercase()
    {
        // Standards 04 § Style — plural lowercase resource nouns. The
        // [controller] token expands to the C# class name, so without
        // RouteOptions.LowercaseUrls a MixedCaseProbeController publishes
        // /api/v1/MixedCaseProbe. A URL is part of the contract and renaming
        // it later is a breaking change under ADR-0024.
        var document = await GetDocumentAsync("v1");

        document.RootElement.GetProperty("paths").EnumerateObject()
            .Select(path => path.Name)
            .Should().Contain("/api/v1/mixedcaseprobe")
            .And.NotContain("/api/v1/MixedCaseProbe");

        var response = await _client.GetAsync(new Uri("/api/v1/mixedcaseprobe", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Document_Declares_No_Servers()
    {
        // `servers` defaults to the request's scheme + Host, which is
        // client-chosen. ADR-0036 fixes that no client input decides anything
        // the API asserts, and the openapi-diff gate needs an artefact that
        // does not change with the caller.
        var document = await GetDocumentAsync("v1");

        document.RootElement.TryGetProperty("servers", out var servers).Should().BeFalse(
            "a host-derived servers entry makes the contract diff against itself");
    }

    [Fact]
    public async Task An_Obsolete_Action_Is_Published_As_Deprecated()
    {
        var document = await GetDocumentAsync("v1");

        document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/obsoleteprobe")
            .GetProperty("get")
            .GetProperty("deprecated").GetBoolean().Should().BeTrue();

        // And a live one carries no `deprecated` key at all: Microsoft.OpenApi
        // omits default-valued properties and OpenAPI reads an absent
        // `deprecated` as false. Asserting the pair is what makes the
        // derivation — rather than a constant — the thing under test.
        document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/versionprobe")
            .GetProperty("get")
            .TryGetProperty("deprecated", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_V2_Operation_Declares_v2_As_The_Major_It_Was_Introduced_In()
    {
        // The v1 assertions alone would pass against a transformer that
        // hardcoded "v1".
        var document = await GetDocumentAsync("v2");

        document.RootElement.GetProperty("paths")
            .GetProperty("/api/v2/versionprobev2")
            .GetProperty("get")
            .GetProperty("x-version-introduced").GetString().Should().Be("v2");
    }

    private static bool IsOperation(JsonProperty member) =>
        member.Name is "get" or "put" or "post" or "delete"
            or "options" or "head" or "patch" or "trace";

    private static IEnumerable<string> TagNames(JsonDocument document) =>
        document.RootElement.TryGetProperty("tags", out var tags)
            ? tags.EnumerateArray().Select(tag => tag.GetProperty("name").GetString() ?? string.Empty)
            : [];

    private async Task<JsonDocument> GetDocumentAsync(string major)
    {
        var response = await _client.GetAsync(new Uri($"/openapi/{major}.json", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}

/// <summary>
/// Host fixture that adds two probe controllers — one declaring no major, one
/// declaring <c>v2</c> — plus a second live major so the per-document
/// filtering has something to filter.
/// </summary>
public sealed class ApiVersioningFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            // Keep only this file's probes — the enforcement probes in
            // VersionedRouteEnforcementTests are deliberately broken and would
            // abort this host. And swap the production convention for one that
            // knows about v2, so the routing layer agrees with the second
            // document registered below; the guard that refuses an unpublished
            // major is exactly what would otherwise reject VersionProbeV2.
            services.AddControllers(options =>
                {
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(VersionProbeController),
                        typeof(VersionProbeV2Controller),
                        typeof(MixedCaseProbeController),
                        typeof(ObsoleteProbeController)));
                    foreach (var production in options.Conventions
                                 .OfType<VersionedRouteConvention>().ToList())
                    {
                        options.Conventions.Remove(production);
                    }

                    options.Conventions.Add(new VersionedRouteConvention([1, 2]));
                })
                .AddApplicationPart(typeof(VersionProbeController).Assembly);

            // A second live major, registered through the SAME production
            // helper the composition root uses. Re-deriving the transformer
            // chain here would let the test pass against wiring production
            // does not have.
            services.AddLearnStackOpenApiDocument(2);
        });
    }
}

// No [Route] here: ApiControllerBase carries [Route("[controller]")] and
// RouteAttribute is Inherited, so declaring a second one would give the
// controller two selectors and two addresses. The [controller] token resolves
// against the DERIVED name, which is exactly the convention Standards 04
// § URL Structure asks for (plural resource nouns -> /api/v1/courses).
public sealed class VersionProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok(new { major = "v1" });
}

[ApiVersion(2)]
public sealed class VersionProbeV2Controller : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok(new { major = "v2" });
}

/// <summary>Proves the published path is lowercased, not the C# class name.</summary>
public sealed class MixedCaseProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

/// <summary>Proves `deprecated` is derived from <see cref="ObsoleteAttribute"/>.</summary>
public sealed class ObsoleteProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    [Obsolete("Probe for the deprecated-derivation rule.")]
    public IActionResult Get() => Ok();
}

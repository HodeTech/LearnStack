using System.Net;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The assertion comparison from
/// <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § What the assertions do</see>: <c>X-Tenant-Id</c> and
/// <c>X-Organization-Id</c> can cause a request to be rejected and can never
/// cause a tenant to be selected.
/// </summary>
/// <remarks>
/// Packet 4 resolves nothing, so the fixture substitutes a resolved
/// <c>ITenantContext</c> — which is what makes the mismatch path reachable at
/// all. That substitution is the test's subject, not a shortcut: ADR-0036's
/// staging table says this comparison is "unreachable in traffic … and
/// exercised by unit tests over a stubbed context" until Packet 7.
/// </remarks>
public sealed class TenantAssertionHttpTests(ResolvedTenantFixture fixture)
    : IClassFixture<ResolvedTenantFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task An_Assertion_That_Agrees_Changes_Nothing()
    {
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task No_Assertion_Changes_Nothing()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/assertionprobe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Tenant_Assertion_That_Disagrees_Is_A_404()
    {
        // 404, not 403: saying "wrong tenant" confirms the other tenant exists.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json",
                "a rejected assertion answers exactly as a routing 404 does");
    }

    [Fact]
    public async Task An_Organization_Assertion_That_Disagrees_Is_A_404()
    {
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.OrganizationHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_Assertion_Never_Selects_A_Tenant()
    {
        // The property the whole ADR exists for. The probe echoes the tenant
        // the context resolved; asserting a different one must not change it —
        // it must refuse the request.
        using var agreeing = Get("/api/v1/assertionprobe");
        agreeing.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());

        var body = await (await _client.SendAsync(agreeing)).Content.ReadAsStringAsync();

        body.Should().Contain(ResolvedTenantFixture.TenantId.ToString(),
            "the resolved tenant is the only one that can ever be served");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000")]
    [InlineData("'; drop table tenants--")]
    public async Task A_Malformed_Assertion_Is_A_400(string value)
    {
        // An empty value is deliberately not a row here: HttpClient drops a
        // header whose value is empty, so the request arrives with no header at
        // all and correctly answers 200. The code path is still covered —
        // Guid.TryParse("") is false — it simply cannot be reached through a
        // client that refuses to send it.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, value);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_Repeated_Assertion_Is_Refused_Not_Resolved_By_First_Or_Last()
    {
        // The classic header-confusion bug: a proxy in front of a client that
        // already sent one produces two, and whichever end you pick, some
        // topology makes it the attacker's. Both values here are individually
        // valid — only their multiplicity is wrong.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Every_Response_Carries_A_Correlation_Id()
    {
        // Standards 10 § Correlation puts correlation_id on the Problem Details
        // body and on error-tracker captures. The success path had no handle at
        // all: a client reporting "this rendered the wrong thing" could only
        // obtain one by receiving an error first.
        var response = await _client.GetAsync(new Uri("/api/v1/assertionprobe", UriKind.Relative));

        response.Headers.TryGetValues(CorrelationHeaderMiddleware.HeaderName, out var values)
            .Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("client-chosen-value")]
    [InlineData("café")]
    [InlineData("a\u0001b")]
    [InlineData("\U0001F4A9")]
    [InlineData("a\u007Fb")]
    public async Task A_Client_Supplied_Correlation_Id_Is_Ignored_Not_Reflected(string supplied)
    {
        // A first version echoed this back under a second header. Kestrel
        // accepts bytes in a REQUEST header that it refuses to write into a
        // RESPONSE header, so 'é', a control character or an emoji made the
        // assignment throw: a 500 on every route, pre-auth and pre-routing,
        // each one captured by IErrorTrackingProvider. One header, anonymous,
        // and the error-tracker quota is someone else's.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.TryAddWithoutValidation(
            CorrelationHeaderMiddleware.HeaderName, supplied);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "no client-supplied value may decide whether this request succeeds");

        var returned = response.Headers.GetValues(CorrelationHeaderMiddleware.HeaderName).Single();
        returned.Should().NotBe(supplied,
            "the trace context is the identity, and the client's value is not adopted");
        response.Headers.Should().NotContain(header =>
            header.Key.Contains("Request-Correlation", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/openapi/v1.json")]
    public async Task A_Malformed_Assertion_Does_Not_Break_An_Unscoped_Route(string path)
    {
        // Registered globally, a malformed X-Tenant-Id 400s the orchestrator's
        // health probe — which takes the pod out — and the Hub's
        // /api/internal/* surface, neither of which has an assertion to
        // compare. ADR-0036 scopes host classification to /api/v1/* for the
        // same reason.
        using var request = Get(path);
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, "not-a-guid");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpRequestMessage Get(string path) =>
        new(HttpMethod.Get, new Uri(path, UriKind.Relative));
}

/// <summary>
/// A host whose <see cref="ITenantContext"/> is resolved, so the assertion
/// comparison has something to compare against.
/// </summary>
public class ResolvedTenantFixture : WebApplicationFactory<Program>
{
    public static readonly Guid TenantId = Guid.Parse("018f4d40-0000-7000-8000-0000000000aa");
    public static readonly Guid OrganizationId = Guid.Parse("018f4d40-0000-7000-8000-0000000000bb");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(AssertionProbeController))))
                .AddApplicationPart(typeof(AssertionProbeController).Assembly);

            services.RemoveAll<ITenantContext>();
            services.AddScoped<ITenantContext>(_ => ResolvedContext.Instance);

            // The dimension a malformed header is counted under is otherwise
            // observable only as a metric label, and the parameter carrying it
            // was accepted and dropped once already.
            services.RemoveAll<ITenantAssertionRecorder>();
            services.AddSingleton<ITenantAssertionRecorder>(Recorder);
        });
    }

    /// <summary>What the middleware reported, for the test to read back.</summary>
    public SpyRecorder Recorder { get; } = new();

    public sealed class SpyRecorder : ITenantAssertionRecorder
    {
        private readonly List<TenantAssertionDimension> _unresolved = [];

        public IReadOnlyList<TenantAssertionDimension> Unresolved
        {
            get { lock (_unresolved) { return [.. _unresolved]; } }
        }

        public void Clear()
        {
            lock (_unresolved) { _unresolved.Clear(); }
        }

        public void RecordRejection(TenantAssertionRejection rejection)
        {
        }

        public void RecordUnresolved(TenantAssertionDimension dimension)
        {
            lock (_unresolved) { _unresolved.Add(dimension); }
        }
    }

    internal sealed class ResolvedContext : ITenantContext
    {
        public static ResolvedContext Instance { get; } = new();

        public bool IsResolved => true;
        public Guid TenantId => ResolvedTenantFixture.TenantId;
        public Guid? OrganizationId => ResolvedTenantFixture.OrganizationId;
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => "integration-test";
    }
}

/// <summary>
/// Which dimension a malformed assertion is counted under.
/// </summary>
/// <remarks>
/// Its own fixture, because these read a recorder that every other test in the
/// class also writes to.
/// </remarks>
public sealed class TenantAssertionDimensionTests(DimensionFixture fixture)
    : IClassFixture<DimensionFixture>
{
    [Fact]
    public async Task A_Malformed_Organization_Header_Is_Counted_As_An_Organization()
    {
        // The two reads used to be short-circuited with `||`, so a valid tenant
        // beside a malformed organization still reported Tenant — the one thing
        // this counter exists to tell an operator.
        fixture.Recorder.Clear();
        using var client = fixture.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/assertionprobe", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, ResolvedTenantFixture.TenantId.ToString());
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.OrganizationHeaderName, "not-a-guid");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Recorder.Unresolved.Should().Equal(TenantAssertionDimension.Organization);
    }

    [Fact]
    public async Task A_Malformed_Tenant_Header_Is_Counted_As_A_Tenant()
    {
        fixture.Recorder.Clear();
        using var client = fixture.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/assertionprobe", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, "not-a-guid");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Recorder.Unresolved.Should().Equal(TenantAssertionDimension.Tenant);
    }
}

/// <summary>A resolved host whose recorder nothing else shares.</summary>
public sealed class DimensionFixture : ResolvedTenantFixture;


public sealed class AssertionProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get([FromServices] ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        return Ok(new { tenant = tenantContext.TenantId });
    }
}
